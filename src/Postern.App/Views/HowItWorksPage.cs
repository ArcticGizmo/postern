using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Postern.Broker;
using Postern.Core.Providers;

namespace Postern.App.Views;

/// <summary>
/// What happens to a request, drawn, and how to move an existing client onto Postern. Everything is filled in from
/// the connection picked at the top (its name, host, injection and this Postern's pipe), so the examples can be
/// copied as they are. Only client patterns already proved in spikes/consumers are shown.
/// </summary>
internal sealed class HowItWorksPage : UserControl
{
    const string AppName = "my-script";

    /// <summary>A typical first call per template: method, path below the base URL, the scope's path, and a JSON body for a POST.</summary>
    sealed record Example(string Method, string Path, string ScopePath, string? Body = null);

    static readonly Dictionary<string, Example> Examples = new()
    {
        ["github"] = new("GET", "/repos/OWNER/REPO/pulls", "/repos/*/*/pulls"),
        ["github-enterprise"] = new("GET", "/repos/OWNER/REPO/pulls", "/repos/*/*/pulls"),
        ["gitlab"] = new("GET", "/projects/PROJECT_ID/merge_requests", "/projects/*/merge_requests"),
        ["bitbucket"] = new("GET", "/repositories/WORKSPACE/REPO/pullrequests", "/repositories/*/*/pullrequests"),
        ["azure-devops"] = new("GET", "/PROJECT/_apis/git/repositories?api-version=7.1", "/*/_apis/git/repositories"),
        ["jira"] = new("GET", "/rest/api/3/issue/KEY-123", "/rest/api/3/issue/*"),
        ["confluence"] = new("GET", "/api/v2/pages/PAGE_ID", "/api/v2/pages/*"),
        ["octopus"] = new("GET", "/api/Spaces-1/deployments", "/api/*/deployments"),
        ["linear"] = new("POST", "/graphql", "/graphql", """{ "query": "{ viewer { name } }" }"""),
        ["sentry"] = new("GET", "/organizations/ORG/issues/", "/organizations/*/issues"),
        ["slack"] = new("POST", "/chat.postMessage", "/chat.postMessage", """{ "channel": "C0123456", "text": "Hello from Postern" }"""),
        ["openai"] = new("POST", "/chat/completions", "/chat/completions", """{ "model": "gpt-4o-mini", "messages": [{ "role": "user", "content": "Hi" }] }"""),
        ["anthropic"] = new("POST", "/v1/messages", "/v1/messages", """{ "model": "claude-sonnet-5-5", "max_tokens": 256, "messages": [{ "role": "user", "content": "Hi" }] }"""),
        ["datadog"] = new("GET", "/api/v1/monitor", "/api/v1/monitor"),
    };

    static readonly Example CustomExample = new("GET", "/ENDPOINT", "/ENDPOINT");

    readonly BrokerRuntime _broker;
    readonly ComboBox _picker = new() { MinWidth = 220 };

    readonly TextBlock _appDetail = Ui.Text("", Ui.Muted, 12);
    readonly TextBlock _pipe = Centered(Ui.Code(""));
    readonly TextBlock _stepConnection = Ui.Text("", size: 12.5);
    readonly TextBlock _stepCredential = Ui.Text("", size: 12.5);
    readonly TextBlock _hostTitle = Ui.Text("", size: 15);
    readonly TextBlock _hostDetail = Ui.Text("", Ui.Muted, 12);

    readonly SelectableTextBlock _sent = CodeText();
    readonly SelectableTextBlock _forwarded = CodeText();
    readonly SelectableTextBlock _mapping = CodeText();
    readonly SelectableTextBlock _addresses = CodeText();
    readonly TextBlock _scopeNote = Ui.Text("", Ui.Muted, 12.5);

    readonly TabControl _code = new();

    public HowItWorksPage(BrokerRuntime broker)
    {
        _broker = broker;
        _picker.SelectionChanged += (_, _) => Render();

        var picker = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { new TextBlock { Text = "Show it for", Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center }, _picker },
        };

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 14,
                Children =
                {
                    Ui.Heading("How it works"),
                    Ui.Text("Apps never hold your credentials. They send ordinary HTTP requests to Postern over a named pipe that only your " +
                            "Windows account can open; Postern checks each one against what you approved for that app, adds the credential, " +
                            "and sends it on over HTTPS. Pick a connection to see its own addresses and examples.", Ui.Muted),
                    picker,
                    Diagram(),
                    Section("On the wire", WireComparison()),
                    Section("Moving a client over", new StackPanel
                    {
                        Spacing = 10,
                        Children =
                        {
                            Ui.Text("Two things change: the base URL becomes http://postern/<connection>/, and the client connects to the pipe " +
                                    "instead of a TCP port. Paths, query strings, bodies and the response stay exactly as the service defines them.", Ui.Muted, 12.5),
                            CodeBlock(_mapping),
                            _scopeNote,
                        },
                    }),
                    Section("Every connection's address", CodeBlock(_addresses)),
                    Section("Examples", new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            Ui.Text("Each registers once (you approve it in Postern), keeps the app token Postern gives it, and sends it with every request. " +
                                    "That token is the app's key to Postern, not your credential: revoke the app under Apps and it stops working at once.", Ui.Muted, 12.5),
                            _code,
                        },
                    }),
                },
            },
        };
        Refresh();
    }

    /// <summary>Re-reads the connections, keeping the one picked if it still exists.</summary>
    public void Refresh()
    {
        var current = _picker.SelectedItem as string ?? ProviderDefinition.GitHub.Name;
        var names = _broker.Connections.All.Select(c => c.Name).ToList();
        _picker.ItemsSource = names;
        _picker.SelectedIndex = Math.Max(names.IndexOf(current), 0);
        Render();
    }

    public void Select(string connection)
    {
        if (_picker.ItemsSource is List<string> names && names.IndexOf(connection) is >= 0 and var index)
            _picker.SelectedIndex = index;
    }

    void Render()
    {
        if (_picker.SelectedItem is not string name || _broker.Connections.Find(name) is not { } connection)
            return;

        var example = ExampleFor(connection);
        var pipe = Program.Args.PipeName;
        var upstreamPath = connection.BaseUrl.AbsolutePath.TrimEnd('/') + example.Path;
        var posternPath = $"/{connection.Name}{example.Path}";
        var scope = ScopeFor(connection, example);
        var labels = connection.Fields.ToDictionary(f => f.Name, f => $"‹{f.Label}›");

        _appDetail.Text = $"Node, .NET, PowerShell…\nasks for http://postern{posternPath}\nwith its own Postern token";
        _pipe.Text = $@"\\.\pipe\{pipe}";
        _pipe.TextWrapping = TextWrapping.NoWrap;
        _pipe.FontSize = 11.5;
        _stepConnection.Text = $"Which connection? /{connection.Name}/… is {connection.Name}: {connection.TemplateName} at {connection.Origin}.";
        _stepCredential.Text = $"Add {connection.Name}'s credential from Windows Credential Manager ({connection.AuthSummary}), only if it was entered for {connection.Origin}.";
        _hostTitle.Text = connection.BaseUrl.Authority;
        _hostDetail.Text = $"{connection.TemplateName}\nsees an ordinary request with your credential, from this machine";

        var query = connection.Apply(labels).Where(i => i.IsQuery).Select(i => $"{i.Name}={i.Value}").ToList();
        var separator = example.Path.Contains('?') ? "&" : "?";
        _sent.Text = string.Join('\n',
        [
            $"{example.Method} {posternPath} HTTP/1.1",
            "Host: postern",
            "Postern-App-Token: ‹this app's token›",
            .. example.Body is null ? Array.Empty<string>() : ["Content-Type: application/json", "", example.Body],
        ]);
        _forwarded.Text = string.Join('\n',
        [
            $"{example.Method} {upstreamPath}{(query.Count == 0 ? "" : separator + string.Join('&', query))} HTTP/1.1",
            $"Host: {connection.BaseUrl.Authority}",
            .. connection.Inject.Where(r => r.Query is null).Select(r => r.Basic is { } basic
                ? $"Authorization: Basic base64({Injection.Expand(basic.Username, labels)}{(basic.Password is null ? "" : ":" + Injection.Expand(basic.Password, labels))})"
                : $"{r.Header}: {Injection.Expand(r.Value!, labels)}"),
            "User-Agent: ‹yours, or Postern's›",
            .. example.Body is null ? Array.Empty<string>() : ["Content-Type: application/json", "", example.Body],
        ]);

        var upstreamUrl = connection.BaseUrl.GetLeftPart(UriPartial.Authority) + upstreamPath;
        _mapping.Text = $"before   {upstreamUrl}\nafter    http://postern{posternPath}    over \\\\.\\pipe\\{pipe}, with Postern-App-Token";
        _scopeNote.Text = $"The app asks for scopes when it registers, and you approve exactly those. This call needs  {scope}  " +
                          "(method, connection, path). * matches one path segment and ** any number; the query string isn't part of the match, " +
                          "and a GET scope also allows HEAD. Ask for the narrowest paths that work.";

        var width = _broker.Connections.All.Max(c => c.Name.Length);
        var baseWidth = _broker.Connections.All.Max(c => c.BaseUrl.ToString().Length);
        _addresses.Text = string.Join('\n', _broker.Connections.All.Select(c =>
            $"{c.Name.PadRight(width)}   {c.BaseUrl.ToString().PadRight(baseWidth)}  →  http://postern/{c.Name}/"));

        var selected = _code.SelectedIndex;
        _code.ItemsSource = null;
        _code.Items.Clear();
        foreach (var (title, code) in CodeFor(connection, pipe))
            _code.Items.Add(Tab(title, code));
        _code.SelectedIndex = Math.Clamp(selected, 0, _code.Items.Count - 1);
    }

    static Example ExampleFor(ProviderDefinition connection) => Examples.GetValueOrDefault(connection.Template ?? "") ?? CustomExample;

    static string ScopeFor(ProviderDefinition connection, Example example) => $"{example.Method} {connection.Name}{example.ScopePath}";

    /// <summary>The page's code examples for a connection, as (tab title, code). postern-snapshots writes them out too, so they can be run as shown.</summary>
    internal static IReadOnlyList<(string Title, string Code)> CodeFor(ProviderDefinition connection, string pipe)
    {
        var example = ExampleFor(connection);
        var scope = ScopeFor(connection, example);
        var examples = new List<(string, string)>
        {
            ("Node", Node(pipe, connection.Name, example, $"/{connection.Name}{example.Path}", scope)),
            (".NET", DotNet(pipe, connection.Name, example, scope)),
        };
        if (connection.Template is "github" or "github-enterprise")
            examples.Add(("Octokit.NET", Octokit(pipe, connection.Name, scope)));
        examples.Add(("PowerShell", PowerShell(connection.Name, example, scope)));
        return examples;
    }

    // ── The diagram ───────────────────────────────────────────────────────────────

    Control Diagram()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,170,1.6*,170,*") };
        Place(grid, 0, Box(Ui.Text("Your app", size: 15), _appDetail, Ui.Border));
        Place(grid, 1, Arrow(_pipe, "plain HTTP, this account only"));
        Place(grid, 2, Box(Ui.Text("Postern", size: 15), new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Step("1", Ui.Text("Who's calling? The app's token, and the program actually on the other end of the pipe.", size: 12.5)),
                Step("2", Ui.Text("Allowed? The method and path must match a scope you approved for that app.", size: 12.5)),
                Step("3", _stepConnection),
                Step("4", _stepCredential),
            },
        }, Ui.Accent));
        Place(grid, 3, Arrow(Centered(Ui.Code("HTTPS")), "your proxy and TLS settings"));
        Place(grid, 4, Box(_hostTitle, _hostDetail, Ui.Border));

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                grid,
                Ui.Text("← The response comes back to the app unchanged, plus Postern-RateLimit-* headers when the service reports its limits. " +
                        "A request that fails any step gets a Postern error instead and never leaves the machine; either way it's in the Audit log.", Ui.Muted, 12),
            },
        };
    }

    static void Place(Grid grid, int column, Control control)
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    static Control Box(Control title, Control body, IBrush border) => new Border
    {
        Background = Ui.Panel,
        BorderBrush = border,
        BorderThickness = new Thickness(border == Ui.Accent ? 1.5 : 1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12),
        Child = new StackPanel { Spacing = 8, Children = { title, body } },
    };

    static Control Step(string number, TextBlock text)
    {
        var badge = new TextBlock { Text = number, Foreground = Ui.Accent, FontWeight = FontWeight.Bold, Width = 16 };
        DockPanel.SetDock(badge, Dock.Left);
        return new DockPanel { Children = { badge, text } };
    }

    static Control Arrow(Control above, string below) => new StackPanel
    {
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0),
        Spacing = 4,
        Children =
        {
            above,
            new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M0,7 L150,7 M142,1 L150,7 L142,13"),
                Stroke = Ui.Accent,
                StrokeThickness = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
            Centered(Ui.Text(below, Ui.Muted, 11)),
        },
    };

    static TextBlock Centered(TextBlock text)
    {
        text.TextAlignment = TextAlignment.Center;
        text.HorizontalAlignment = HorizontalAlignment.Center;
        return text;
    }

    Control WireComparison()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        Place(grid, 0, Ui.Field("What the app sends, over the pipe", CodeBlock(_sent, minLines: 8)));
        Place(grid, 2, Ui.Field("What Postern sends, over HTTPS", CodeBlock(_forwarded, minLines: 8)));
        return grid;
    }

    // ── Code ──────────────────────────────────────────────────────────────────────

    static SelectableTextBlock CodeText() => new() { FontFamily = Ui.Mono, FontSize = 12.5, Foreground = Ui.Foreground };

    static Control CodeBlock(SelectableTextBlock text, int minLines = 0) => new Border
    {
        Background = Ui.Sunken,
        BorderBrush = Ui.Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 10),
        MinHeight = minLines * 17 + 20,
        Child = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = text },
    };

    TabItem Tab(string title, string code)
    {
        var text = CodeText();
        text.Text = code;
        var copy = Ui.Button("Copy", async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(code);
        });
        copy.HorizontalAlignment = HorizontalAlignment.Right;
        return new TabItem
        {
            Header = title,
            FontSize = 14,
            Content = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { copy, CodeBlock(text) } },
        };
    }

    static Control Section(string title, Control body) => new StackPanel
    {
        Spacing = 8,
        Margin = new Thickness(0, 6, 0, 0),
        Children = { new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ui.Foreground }, body },
    };

    static string Node(string pipe, string connection, Example example, string posternPath, string scope) => $$"""
        // node {{AppName}}.mjs   (Node 18 or later, no packages)
        import http from 'node:http';

        // Postern listens on a named pipe, not a port: point node:http at the pipe and address requests to "postern".
        function postern(method, path, { token, body } = {}) {
          const payload = body === undefined ? undefined : JSON.stringify(body);
          return new Promise((resolve, reject) => {
            const req = http.request({
              socketPath: '\\\\.\\pipe\\{{pipe}}',
              method,
              path,
              headers: {
                Host: 'postern',
                ...(token && { 'Postern-App-Token': token }),
                ...(payload && { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(payload) }),
              },
            }, (res) => {
              let text = '';
              res.on('data', (chunk) => (text += chunk));
              res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: text }));
            });
            req.on('error', reject);
            req.end(payload);
          });
        }

        // Once: ask for exactly what this script needs. Postern shows you an approval prompt and this waits for it.
        async function register(name, scopes) {
          const begin = await postern('POST', '/_postern/register', { body: { name, scopes } });
          if (begin.headers['postern-error']) throw new Error(begin.body);
          const { id, claim } = JSON.parse(begin.body);
          for (;;) {
            const res = await postern('POST', `/_postern/register/${id}/claim?wait=30`, { body: { claim } });
            if (res.headers['postern-error']) throw new Error(res.body);
            if (res.status === 200) return JSON.parse(res.body).token;
          }
        }

        // Keep the token somewhere private and reuse it; registering again asks you again.
        const token = process.env.POSTERN_TOKEN ?? await register('{{AppName}}', ['{{scope}}']);

        // https://…{{example.Path}} on {{connection}} is now {{posternPath}} on the pipe.
        const res = await postern('{{example.Method}}', '{{posternPath}}', { token{{(example.Body is null ? "" : $", body: {example.Body}")}} });
        // A refusal from Postern (not the service) carries a postern-error header: scope-denied, credential-missing, ...
        console.log(res.status, res.headers['postern-error'] ?? '', res.body.slice(0, 300));
        """;

    static string DotNet(string pipe, string connection, Example example, string scope)
    {
        var options = pipe == BrokerOptions.DefaultPipeName ? $"AppName = \"{AppName}\"" : $"AppName = \"{AppName}\", PipeName = \"{pipe}\"";
        var relative = example.Path.TrimStart('/');
        var call = example.Body is null
            ? $"await http.{(example.Method == "GET" ? "GetAsync" : "DeleteAsync")}(\"{relative}\")"
            : $"await http.PostAsync(\"{relative}\", new StringContent(\"\"\"{example.Body}\"\"\", Encoding.UTF8, \"application/json\"))";
        return $$"""
            // Reference Postern.Client (Windows). It dials the pipe, checks the server really is your Postern,
            // keeps the app token with DPAPI, and rides out a Postern restart.
            using System.Text;
            using Postern.Client;

            var postern = new PosternClient(new PosternOptions { {{options}} });

            // Once: ask for exactly what this app needs. Postern shows you an approval prompt and this waits for it.
            if (!postern.HasToken)
                await postern.RegisterAsync(["{{scope}}"]);

            // Relative URIs are paths on {{connection}}: this is http://postern/{{connection}}/{{relative}} over the pipe.
            using var http = postern.CreateHttpClient("{{connection}}");
            using var response = {{call}};

            // A refusal from Postern (not the service) has a PosternError: scope-denied, credential-missing, ...
            if (await response.GetPosternErrorAsync() is { } refused)
                Console.WriteLine($"Postern said {refused.Code}: {refused.Detail}");
            else
                Console.WriteLine($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            """;
    }

    static string Octokit(string pipe, string connection, string scope)
    {
        var options = pipe == BrokerOptions.DefaultPipeName ? $"AppName = \"{AppName}\"" : $"AppName = \"{AppName}\", PipeName = \"{pipe}\"";
        return $$"""
            // Octokit.NET with Postern.Client: give it Postern's handler and an http://postern/ base address.
            using Octokit;
            using Octokit.Internal;
            using Postern.Client;

            var postern = new PosternClient(new PosternOptions { {{options}} });
            if (!postern.HasToken)
                await postern.RegisterAsync(["{{scope}}"]);

            // Use GitHubClient(IConnection): the (ProductHeaderValue, Uri) constructor appends /api/v3/ to any address
            // that isn't github.com. Octokit sends no credential of its own; Postern adds yours.
            var connection = new Connection(
                new ProductHeaderValue("{{AppName}}"),
                new Uri("http://postern/{{connection}}/"),
                new InMemoryCredentialStore(Credentials.Anonymous),
                new HttpClientAdapter(postern.CreateHandler),
                new SimpleJsonSerializer());
            var github = new GitHubClient(connection);

            var pulls = await github.PullRequest.GetAllForRepository("OWNER", "REPO");
            Console.WriteLine($"{pulls.Count} open pull requests");

            // Archive and log downloads redirect to pre-signed URLs on other hosts. Postern won't carry those:
            // read the Location header and fetch it directly; it needs no credential.
            """;
    }

    static string PowerShell(string connection, Example example, string scope)
    {
        var data = example.Body is null ? "" : $" --data '{example.Body}'";
        return $$"""
            # postern-cli talks to Postern for scripts (curl can't open a named pipe on Windows).
            # The installed copy is here; a dev build's is in src\Postern.Cli\bin\Debug\net10.0-windows.
            $cli = "$env:LOCALAPPDATA\Postern\current\postern-cli.exe"

            # Once: ask for exactly what the script needs. Postern shows you an approval prompt.
            & $cli register --name {{AppName}} --scope "{{scope}}"

            # Then, as often as you like. The path is the connection name plus the service's own path.
            & $cli call --name {{AppName}} {{example.Method}} {{connection}}{{example.Path}}{{data}}
            """;
    }
}

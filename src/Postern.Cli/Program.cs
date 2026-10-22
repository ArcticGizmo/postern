using System.Net.Http.Headers;
using System.Text;
using Postern.Client;

// postern-cli: register once, then call providers through Postern.
//
//   postern-cli register --name scripts --scope "GET github/user" --scope "POST github/graphql"
//   postern-cli call --name scripts GET github/user
//   postern-cli call --name scripts POST github/graphql --data '{"query":"{ viewer { login } }"}'
//   postern-cli status --name scripts
//   postern-cli forget --name scripts
//   postern-cli register --name scripts --scope "GET github/user" --wait 120   (Postern may still be starting)
//
// Every registration made through the CLI has the same exe (postern-cli.exe), so the name is what tells them apart
// in the approval prompt. Tokens are stored per name, DPAPI-protected.

const int ExitOk = 0, ExitHttpError = 1, ExitUsage = 2, ExitPostern = 3;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    return Usage();
if (args[0] == "credential")
    return Postern.Cli.CredentialCommand.Run(args[1..], Console.WriteLine, Console.Error.WriteLine);

var command = args[0];
var rest = args.Skip(1).ToList();
string? Take(string name)
{
    var i = rest.IndexOf(name);
    if (i < 0 || i + 1 >= rest.Count)
        return null;
    var value = rest[i + 1];
    rest.RemoveRange(i, 2);
    return value;
}
List<string> TakeAll(string name)
{
    var values = new List<string>();
    while (Take(name) is { } value)
        values.Add(value);
    return values;
}
bool Flag(string name) => rest.Remove(name);

var appName = Take("--name") ?? "cli";
var pipe = Take("--pipe") ?? new PosternOptions { AppName = appName }.PipeName;
var waitText = Take("--wait");
if (waitText is not null && (!int.TryParse(waitText, out var waitSeconds) || waitSeconds < 0))
    return Usage();
using var postern = new PosternClient(new PosternOptions
{
    AppName = appName,
    PipeName = pipe,
    TokenStore = DpapiFileTokenStore.ForApp(PosternClient.TokenKey("postern-cli." + appName, pipe)),
});

try
{
    if (command is "status" or "register" or "call" && !postern.IsAvailable)
    {
        if (waitText is null)
        {
            Console.Error.WriteLine($"postern: nothing is listening on \\\\.\\pipe\\{pipe}. {PosternClient.NotRunningHint}");
            Console.Error.WriteLine("postern: or pass --wait SECONDS to wait for it to start.");
            return ExitPostern;
        }
        Console.Error.WriteLine($"postern: waiting up to {waitText} s for Postern on \\\\.\\pipe\\{pipe}...");
        if (!await postern.WaitUntilAvailableAsync(TimeSpan.FromSeconds(int.Parse(waitText))))
        {
            Console.Error.WriteLine($"postern: it didn't start. {PosternClient.NotRunningHint}");
            return ExitPostern;
        }
    }

    switch (command)
    {
        case "status":
            return await Status();
        case "register":
            return await Register(TakeAll("--scope"), Take("--timeout"));
        case "call":
            return await Call();
        case "forget":
            postern.ForgetToken();
            Console.WriteLine($"Forgot the token for '{appName}'.");
            return ExitOk;
        default:
            return Usage();
    }
}
catch (PosternUnavailableException ex)
{
    Console.Error.WriteLine("postern: " + ex.Message);
    return ExitPostern;
}

async Task<int> Status()
{
    var status = await postern.GetStatusAsync();
    Console.WriteLine($"Postern {status.Version} on \\\\.\\pipe\\{status.Pipe} (pid {status.ProcessId}), providers: {string.Join(", ", status.Providers)}");
    if (status.App is { } app)
    {
        Console.WriteLine($"'{app.Name}' is {app.Status}; scopes:");
        foreach (var scope in app.Scopes)
            Console.WriteLine("  " + scope);
    }
    else
    {
        Console.WriteLine(postern.HasToken ? $"'{appName}' has a token, but Postern doesn't recognise it. Register again." : $"'{appName}' isn't registered.");
    }
    return ExitOk;
}

async Task<int> Register(List<string> scopes, string? timeout)
{
    if (scopes.Count == 0)
    {
        Console.Error.WriteLine("register needs at least one --scope \"METHOD provider/path\"");
        return ExitUsage;
    }
    Console.WriteLine($"Asking Postern to approve '{appName}' for:");
    foreach (var scope in scopes)
        Console.WriteLine("  " + scope);
    Console.WriteLine("Approve it in the Postern window...");
    var result = await postern.RegisterAsync(scopes, timeout is null ? null : TimeSpan.FromSeconds(int.Parse(timeout)));
    Console.WriteLine($"{result.Outcome}: {result.Detail}");
    return result.Outcome == RegistrationOutcome.Approved ? ExitOk : ExitPostern;
}

async Task<int> Call()
{
    var include = Flag("-i") | Flag("--include");
    var data = Take("--data");
    var headers = TakeAll("-H");
    if (rest.Count != 2)
        return Usage();

    var method = new HttpMethod(rest[0].ToUpperInvariant());
    var target = rest[1].TrimStart('/');
    var slash = target.IndexOf('/');
    if (slash <= 0)
    {
        Console.Error.WriteLine("call target must be provider/path, e.g. github/user");
        return ExitUsage;
    }

    using var http = new HttpClient(postern.CreateHandler()) { BaseAddress = new Uri("http://postern/") };
    using var request = new HttpRequestMessage(method, target);
    foreach (var header in headers)
    {
        var colon = header.IndexOf(':');
        request.Headers.TryAddWithoutValidation(header[..colon].Trim(), header[(colon + 1)..].Trim());
    }
    if (data is not null)
    {
        var body = data == "-" ? await Console.In.ReadToEndAsync() : data.StartsWith('@') ? await File.ReadAllTextAsync(data[1..]) : data;
        request.Content = new StringContent(body, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[') ? "application/json" : "text/plain");
    }

    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    if (include)
    {
        Console.WriteLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            Console.WriteLine($"{name}: {string.Join(", ", values)}");
        Console.WriteLine();
    }

    if (await response.GetPosternErrorAsync() is { } error)
    {
        Console.Error.WriteLine($"postern: {error.Status} {error.Code}: {error.Detail}");
        if (error.NeedsRegistration)
            Console.Error.WriteLine($"postern: run 'postern-cli register --name {appName} --scope \"...\"'");
        return ExitPostern;
    }

    await using var stdout = Console.OpenStandardOutput();
    await response.Content.CopyToAsync(stdout);
    await stdout.FlushAsync();
    if (!include && Console.IsOutputRedirected is false)
        Console.WriteLine();
    return response.IsSuccessStatusCode || (int)response.StatusCode is >= 300 and < 400 ? ExitOk : ExitHttpError;
}

static int Usage()
{
    Console.Error.WriteLine("""
        postern-cli register [--name N] --scope "METHOD provider/path-glob" [--scope ...] [--timeout SECONDS]
        postern-cli call     [--name N] METHOD provider/path [--data TEXT|@file|-] [-H "Name: value"] [-i]
        postern-cli status   [--name N]
        postern-cli forget   [--name N]
        postern-cli credential set|delete <provider> | credential list   [--data-dir DIR]
                    (secret from stdin or a hidden prompt; stored in Windows Credential Manager for Postern)
        Options: --pipe NAME (default %POSTERN_PIPE% or "postern"). --name defaults to "cli".
                 --wait SECONDS: if Postern isn't running yet, wait for it (status, register, call).
        Exit: 0 ok, 1 provider returned an error, 2 usage, 3 Postern refused or isn't running.
        """);
    return ExitUsage;
}

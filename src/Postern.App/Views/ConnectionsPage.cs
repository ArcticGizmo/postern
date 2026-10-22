using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Postern.Broker;
using Postern.Core.Providers;

namespace Postern.App.Views;

/// <summary>
/// One card per connection: what it talks to, whether its credential is stored, and buttons that open a dialog for
/// each change, so the page itself never rearranges under the pointer. Adding a connection and editing providers.json
/// both happen here, so nobody has to find the data folder.
/// Write-only: Postern never shows a stored secret back, so there's nothing on screen to shoulder-surf or screenshot.
/// </summary>
internal sealed class ConnectionsPage : UserControl
{
    readonly BrokerRuntime _broker;
    readonly StackPanel _list = new() { Spacing = 10 };
    readonly TextBlock _banner = Ui.Text("", Ui.Warning, 12.5);

    public ConnectionsPage(BrokerRuntime broker)
    {
        _broker = broker;

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { Ui.Button("Edit as JSON", () => EditJson()), Ui.Button("Add connection", AddConnection, primary: true) },
        };
        DockPanel.SetDock(actions, Dock.Right);

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 12,
                Children =
                {
                    new DockPanel { Children = { actions, Ui.Heading("Connections") } },
                    Ui.Text("A connection is a service plus one stored credential. Apps ask for scopes on a connection by name, like " +
                            "GET github-work/repos/**, so a second account is a second connection. Credentials are kept in Windows " +
                            "Credential Manager for your account on this machine, only ever sent to the host they were entered for, and never shown again.", Ui.Muted),
                    _banner,
                    _list,
                },
            },
        };
        Refresh();
    }

    /// <summary>Connections without a usable credential, for the nav badge: requests through them fail until one is set.</summary>
    public int Missing { get; private set; }

    public event Action? Changed;

    public void Refresh()
    {
        _banner.Text = _broker.Connections.LoadError is { } error
            ? $"providers.json couldn't be used, so only the built-in github connection is available until it's fixed (Edit as JSON): {error}"
            : "";
        _banner.IsVisible = _banner.Text.Length > 0;

        _list.Children.Clear();
        Missing = 0;
        foreach (var connection in _broker.Connections.All)
            _list.Children.Add(Row(connection));
        Changed?.Invoke();
    }

    async void AddConnection()
    {
        var outcome = await new AddConnectionDialog(_broker).ShowOver<AddOutcome>(this);
        if (outcome is { WantsJson: true })
        {
            EditJson(WithCustomExample());
        }
        else if (outcome?.Added is { } name)
        {
            Refresh();
            if (_broker.Connections.Find(name) is { } added)
                SetCredential(added, replacing: false); // straight on to the credential, which is the point of adding it
        }
    }

    async void EditJson(string? text = null)
    {
        if (await new ProvidersJsonDialog(_broker, text).ShowOver<bool?>(this) == true)
            Refresh();
    }

    async void SetCredential(ProviderDefinition connection, bool replacing)
    {
        if (await new CredentialDialog(_broker, connection, replacing).ShowOver<bool?>(this) == true)
            Refresh();
    }

    async void ClearCredential(ProviderDefinition connection)
    {
        var confirm = new ConfirmDialog($"Clear {connection.Name}'s credential?",
            $"Requests through {connection.Name} fail until a credential is set again. The connection itself stays.", "Clear");
        if (await confirm.ShowOver<bool?>(this) != true)
            return;
        _broker.Credentials.Delete(connection.CredentialTarget);
        Refresh();
    }

    async void RemoveConnection(ProviderDefinition connection)
    {
        var confirm = new ConfirmDialog($"Remove {connection.Name}?",
            $"Its credential is deleted too. Apps with scopes on {connection.Name} keep their approval, but their requests fail until a connection with that name exists again.",
            "Remove");
        if (await confirm.ShowOver<bool?>(this) != true)
            return;
        try
        {
            _broker.Connections.Remove(connection.Name);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            _banner.Text = "Couldn't remove it: " + ex.Message;
            _banner.IsVisible = true;
            return;
        }
        Refresh();
    }

    // The current file plus a custom entry to adapt, or the file as it is when it doesn't parse (there's a fix to make first).
    string? WithCustomExample()
    {
        string json;
        try
        {
            json = _broker.Connections.ReadJson();
        }
        catch (IOException)
        {
            return null; // the editor reports the read failure itself
        }
        try
        {
            var entries = ProvidersFile.Parse(json).ToList();
            entries.Add(new ProviderEntry
            {
                Name = AddConnectionDialog.UnusedName(_broker, "my-service"),
                BaseUrl = "https://api.example.com",
                Fields = [new CredentialField("token", "API token")],
                Inject = [Injection.ToHeader("Authorization", "Bearer {token}")],
            });
            return ProvidersFile.Serialize(entries);
        }
        catch (ArgumentException)
        {
            return json;
        }
    }

    Control Row(ProviderDefinition connection)
    {
        bool stored;
        bool usable;
        string state;
        try
        {
            var blob = _broker.Credentials.Read(connection.CredentialTarget);
            stored = blob is not null;
            string? problem = null;
            usable = blob is not null && connection.DecodeCredential(blob, out problem) is not null;
            state = blob is null ? "missing: requests through this connection fail until you set it"
                : usable ? "stored"
                : $"stored, but it {problem}. Set it again.";
        }
        catch (Exception ex)
        {
            stored = usable = false;
            state = "can't read Credential Manager: " + ex.Message;
        }
        if (!usable)
            Missing++;

        var clear = Ui.Button("Clear", () => ClearCredential(connection));
        clear.IsEnabled = stored;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { Ui.Button(stored ? "Replace" : "Set", () => SetCredential(connection, stored), primary: !usable), clear },
        };
        // The built-in github is always there; an override of it can be undone in the JSON editor.
        var remove = Ui.Button("Remove", () => RemoveConnection(connection));
        remove.IsEnabled = connection.Name != ProviderDefinition.GitHub.Name;
        buttons.Children.Add(remove);

        var header = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { Ui.Text(connection.Name, size: 15), Ui.Text($"{connection.TemplateName} · {connection.AuthSummary}", Ui.Muted, 12) },
                },
                Ui.Code(connection.BaseUrl.ToString(), Ui.Muted),
            },
        });

        return Ui.Card(new StackPanel
        {
            Spacing = 8,
            Children = { header, Ui.Text(state, usable ? Ui.Ok : Ui.Warning, 12) },
        });
    }
}

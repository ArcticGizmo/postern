using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Postern.Broker;
using Postern.Core.Providers;

namespace Postern.App.Views;

/// <summary>
/// One row per provider: whether a credential is stored, and Set / Replace / Remove. Write-only: Postern never shows
/// a stored secret back, so there's nothing on screen to shoulder-surf or screenshot.
/// </summary>
internal sealed class CredentialsWindow : Window
{
    readonly BrokerRuntime _broker;
    readonly StackPanel _list = new() { Spacing = 10 };

    public CredentialsWindow(BrokerRuntime broker)
    {
        _broker = broker;
        Title = "Postern: credentials";
        Width = 640;
        Height = 520;
        Background = Ui.Background;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = TrayArt.WindowIcon();

        var dataDir = Program.Args.DataDirectory;
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 12,
                Children =
                {
                    Ui.Heading("Credentials"),
                    Ui.Text("Stored in Windows Credential Manager for your account on this machine only. Apps never receive them.", Ui.Muted),
                    _list,
                    Ui.Text($"Add providers (Octopus, Jira, ...) in {Path.Combine(dataDir, "providers.json")} and restart Postern.", Ui.Muted, 12),
                },
            },
        };
        Refresh();
    }

    void Refresh()
    {
        _list.Children.Clear();
        foreach (var provider in _broker.Providers)
            _list.Children.Add(Row(provider));
    }

    Control Row(ProviderDefinition provider)
    {
        bool stored;
        string state;
        try
        {
            stored = _broker.Credentials.Read(provider.CredentialTarget) is not null;
            state = stored ? "stored" : "missing: requests to this provider fail until you add one";
        }
        catch (Exception ex)
        {
            stored = false;
            state = "can't read Credential Manager: " + ex.Message;
        }

        var entry = new TextBox
        {
            PasswordChar = '•',
            PlaceholderText = provider.Name == "github" ? "Paste a fine-grained personal access token" : "Paste the secret",
            FontFamily = Ui.Mono,
        };
        var save = Ui.Button("Save", () => { }, primary: true);
        var message = Ui.Text("", Ui.Warning, 12);
        message.IsVisible = false;

        var entryRow = new DockPanel { LastChildFill = true, IsVisible = false };
        DockPanel.SetDock(save, Dock.Right);
        save.Margin = new Thickness(8, 0, 0, 0);
        entryRow.Children.Add(save);
        entryRow.Children.Add(entry);

        void Say(string text)
        {
            message.Text = text;
            message.IsVisible = true;
        }

        var set = Ui.Button(stored ? "Replace" : "Set", () =>
        {
            entryRow.IsVisible = true;
            entry.Focus();
        });
        save.Click += (_, _) =>
        {
            var secret = entry.Text?.Trim().Trim((char)0xFEFF) ?? ""; // a pasted BOM is invisible but breaks the header
            entry.Text = "";
            if (ProviderDefinition.CheckSecret(secret) is { } problem)
            {
                Say($"Not saved: the secret {problem}.");
                return;
            }
            try
            {
                _broker.Credentials.Write(provider.CredentialTarget, secret);
                Refresh();
            }
            catch (Exception ex)
            {
                Say("Couldn't save: " + ex.Message);
            }
        };
        var remove = Ui.Button("Remove", () =>
        {
            _broker.Credentials.Delete(provider.CredentialTarget);
            Refresh();
        });
        remove.IsVisible = stored;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { set, remove } };
        var header = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Ui.Text(provider.Name, size: 15),
                Ui.Code($"{provider.BaseUrl} ({provider.Auth.ToString().ToLowerInvariant()} auth)", Ui.Muted),
            },
        });

        return Ui.Card(new StackPanel
        {
            Spacing = 8,
            Children = { header, Ui.Text(state, stored ? Ui.Ok : Ui.Warning, 12), entryRow, message },
        });
    }
}

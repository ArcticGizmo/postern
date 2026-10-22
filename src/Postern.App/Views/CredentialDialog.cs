using Avalonia.Controls;
using Postern.Broker;
using Postern.Core.Providers;

namespace Postern.App.Views;

/// <summary>Sets or replaces one connection's credential, one input per field. Closes with true once it's stored.</summary>
internal sealed class CredentialDialog : Dialog
{
    readonly BrokerRuntime _broker;
    readonly ProviderDefinition _connection;
    readonly Dictionary<string, TextBox> _boxes;

    public CredentialDialog(BrokerRuntime broker, ProviderDefinition connection, bool replacing) : base($"credential for {connection.Name}", 520)
    {
        _broker = broker;
        _connection = connection;
        _boxes = connection.Fields.ToDictionary(f => f.Name, f => new TextBox
        {
            PasswordChar = f.Secret ? '•' : default,
            PlaceholderText = f.Secret ? "Paste it here" : "",
            FontFamily = Ui.Mono,
        });

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Ui.Field("Sent to", Ui.Code($"{connection.Origin}  ({connection.TemplateName} · {connection.AuthSummary})", Ui.Muted)));
        if (ProviderTemplates.Find(connection.Template) is { } template)
            body.Children.Add(Ui.Text(template.Hint, Ui.Muted, 12));
        foreach (var field in connection.Fields)
            body.Children.Add(Ui.Field(field.Label, _boxes[field.Name]));
        body.Children.Add(Ui.Text(replacing ? "This replaces what's stored. Postern never shows a stored credential back." : "Postern never shows a stored credential back.", Ui.Muted, 12));

        var save = Ui.Button("Save", Save, primary: true);
        save.IsDefault = true;
        Build($"{(replacing ? "Replace" : "Set")} the credential for {connection.Name}", body, CancelButton(), save);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _boxes[_connection.Fields[0].Name].Focus();
    }

    void Save()
    {
        // A pasted BOM is invisible but breaks the header.
        var values = _boxes.ToDictionary(b => b.Key, b => b.Value.Text?.Trim().Trim((char)0xFEFF) ?? "");
        try
        {
            _broker.Credentials.Write(_connection.CredentialTarget, _connection.EncodeCredential(values));
        }
        catch (Exception ex)
        {
            Fail("Not saved: " + ex.Message);
            return;
        }
        Close(true);
    }
}

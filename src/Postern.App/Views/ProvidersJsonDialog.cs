using Avalonia.Controls;
using Avalonia.Media;
using Postern.Broker;

namespace Postern.App.Views;

/// <summary>providers.json, edited in the app. Saving validates first; a file that doesn't validate isn't written. Closes with true once saved.</summary>
internal sealed class ProvidersJsonDialog : Dialog
{
    readonly BrokerRuntime _broker;
    readonly TextBox _json = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = Ui.Mono,
        FontSize = 12.5,
        Height = 380,
    };

    public ProvidersJsonDialog(BrokerRuntime broker, string? text = null) : base("providers.json", 780)
    {
        _broker = broker;
        try
        {
            _json.Text = text ?? broker.Connections.ReadJson();
        }
        catch (IOException ex)
        {
            Fail("Couldn't read it: " + ex.Message);
        }

        Build("Edit connections as JSON", new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Text("Every connection besides the built-in github (an entry named github replaces it). Use \"template\" for a known " +
                        "service, or give \"fields\" and \"inject\" for anything else. Values in inject are templates over the fields; " +
                        "\"query\" puts a value in the URL, which upstream logs will record, so prefer a header when the API allows it.", Ui.Muted, 12),
                Ui.Code(CustomExample, Ui.Muted),
                _json,
                Ui.Text("A connection that moves to another host keeps its credential, but Postern won't send it until it's entered again. " +
                        "Removing a connection deletes its credential.", Ui.Muted, 12),
            },
        }, CancelButton(), Ui.Button("Save", Save, primary: true));
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _json.Focus();
    }

    void Save()
    {
        try
        {
            _broker.Connections.Save(_json.Text ?? "");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            Fail(ex.Message);
            return;
        }
        Close(true);
    }

    const string CustomExample = """
        { "name": "my-service", "baseUrl": "https://api.example.com",
          "fields": [ { "name": "token", "label": "API token" } ],
          "inject": [ { "header": "Authorization", "value": "Bearer {token}" },
                      { "header": "X-Api-Version", "value": "2024-01-01" } ] }
        """;
}

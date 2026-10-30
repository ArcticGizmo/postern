using Avalonia.Controls;
using Postern.Broker;
using Postern.Core.Providers;
using Postern.Core.Scopes;

namespace Postern.App.Views;

/// <summary>What the add dialog ended with: a connection it added, or a request to write a custom one as JSON.</summary>
internal sealed record AddOutcome(string? Added, bool WantsJson);

/// <summary>Adds a connection from a template: pick the service, name it, fill in its host or site.</summary>
internal sealed class AddConnectionDialog : Dialog
{
    const string CustomChoice = "Custom (write it as JSON)";

    readonly BrokerRuntime _broker;
    readonly ComboBox _template = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    readonly TextBlock _hint = Ui.Text("", Ui.Muted, 12);
    readonly TextBox _name = new() { FontFamily = Ui.Mono, PlaceholderText = "lowercase, digits and '-'" };
    readonly Control _nameField;
    readonly StackPanel _parameters = new() { Spacing = 12 };
    readonly Dictionary<string, TextBox> _parameterBoxes = [];
    readonly Button _add;

    public AddConnectionDialog(BrokerRuntime broker, string? templateId = null) : base("add a connection", 520)
    {
        _broker = broker;
        _nameField = Ui.Field("Name apps use in their scopes", _name);
        _add = Ui.Button("Add", Add, primary: true);
        _add.IsDefault = true;

        _template.ItemsSource = ProviderTemplates.All.Select(t => t.DisplayName).Append(CustomChoice).ToList();
        _template.SelectedIndex = Math.Max(ProviderTemplates.All.ToList().FindIndex(t => t.Id == templateId), 0);
        _template.SelectionChanged += (_, _) => TemplateChanged();
        TemplateChanged();

        Build("Add a connection", new StackPanel
        {
            Spacing = 12,
            Children = { Ui.Field("Service", _template), _hint, _nameField, _parameters },
        }, CancelButton(), _add);
    }

    ProviderTemplate? Selected => _template.SelectedIndex is var i && i >= 0 && i < ProviderTemplates.All.Count ? ProviderTemplates.All[i] : null;

    void TemplateChanged()
    {
        _parameters.Children.Clear();
        _parameterBoxes.Clear();
        var template = Selected;
        _nameField.IsVisible = template is not null;
        _add.Content = template is null ? "Open the JSON editor" : "Add";
        if (template is null)
        {
            _hint.Text = "For a service without a template: you write its base URL, the fields its credential has, and where each goes.";
            return;
        }

        _hint.Text = template.Hint;
        _name.Text = UnusedName(_broker, template.Id);
        foreach (var parameter in template.Parameters)
        {
            var box = new TextBox { FontFamily = Ui.Mono, Text = parameter.Default ?? "", PlaceholderText = parameter.Example ?? "" };
            _parameterBoxes[parameter.Name] = box;
            _parameters.Children.Add(Ui.Field(parameter.Label, box));
        }
    }

    public static string UnusedName(BrokerRuntime broker, string baseName)
    {
        var name = baseName;
        for (var n = 2; broker.Connections.Find(name) is not null; n++)
            name = $"{baseName}-{n}";
        return name;
    }

    void Add()
    {
        if (Selected is not { } template)
        {
            Close(new AddOutcome(null, WantsJson: true));
            return;
        }

        var name = _name.Text?.Trim() ?? "";
        if (!ProviderName.IsValid(name))
        {
            Fail("The name must be lowercase letters, digits and '-', starting with a letter (up to 32).");
            return;
        }
        try
        {
            var values = _parameterBoxes.ToDictionary(p => p.Key, p => p.Value.Text ?? "");
            _broker.Connections.Add(new ProviderEntry
            {
                Name = name,
                Template = template.Id,
                BaseUrl = template.Parameters.Count == 0 ? null : template.ResolveUrl(values).OriginalString,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            Fail(ex.Message);
            return;
        }
        Close(new AddOutcome(name, WantsJson: false));
    }
}

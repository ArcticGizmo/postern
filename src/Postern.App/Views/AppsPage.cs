using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Postern.Broker;
using Postern.Core.Apps;

namespace Postern.App.Views;

/// <summary>Every registration, newest first. Approved apps can be revoked; the effect is immediate (next request).</summary>
internal sealed class AppsPage : UserControl
{
    readonly BrokerRuntime _broker;
    readonly StackPanel _list = new() { Spacing = 10 };
    readonly CheckBox _showOld = new() { Content = "Show denied, revoked and replaced", Foreground = Ui.Muted };

    public AppsPage(BrokerRuntime broker)
    {
        _broker = broker;
        _showOld.IsCheckedChanged += (_, _) => Refresh();
        Content = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                Ui.Top(new StackPanel
                {
                    Spacing = 6,
                    Margin = new Thickness(0, 0, 0, 12),
                    Children = { Ui.Heading("Apps"), Ui.Text("Apps you've allowed to call through Postern, and what each may call.", Ui.Muted), _showOld },
                }),
                new ScrollViewer { Content = _list },
            },
        };
        Refresh();
    }

    public void Refresh()
    {
        _list.Children.Clear();
        var showOld = _showOld.IsChecked == true;
        var apps = _broker.Registrations.All()
            .Where(r => showOld || r.Status is RegistrationStatus.Approved or RegistrationStatus.Pending)
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
        if (apps.Count == 0)
            _list.Children.Add(Ui.Text(showOld ? "No registrations yet." : "No apps have access. Apps register themselves; you'll be asked to approve each one.", Ui.Muted));
        foreach (var app in apps)
            _list.Children.Add(Row(app));
    }

    Control Row(AppRegistration app)
    {
        var status = Ui.Text(app.Status.ToString().ToLowerInvariant(), app.Status switch
        {
            RegistrationStatus.Approved => Ui.Ok,
            RegistrationStatus.Pending => Ui.Warning,
            _ => Ui.Muted,
        }, 12);

        var header = new DockPanel { LastChildFill = true };
        if (app.Status == RegistrationStatus.Approved)
        {
            var revoke = Ui.Button("Revoke", () => _broker.Registrations.Revoke(app.Id));
            DockPanel.SetDock(revoke, Dock.Right);
            header.Children.Add(revoke);
        }
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { Ui.Text(app.Name, size: 15), status },
        });

        var scopes = new StackPanel { Spacing = 2 };
        foreach (var scope in app.Scopes)
            scopes.Children.Add(Ui.Code(scope.ToString()));

        var when = $"registered {app.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}" +
                   (app.DecidedAt is { } d ? $", {app.Status.ToString().ToLowerInvariant()} {d.ToLocalTime():yyyy-MM-dd HH:mm}" : "") +
                   (app.Status == RegistrationStatus.Approved && app.TokenHash is null ? " (token not collected yet)" : "");

        return Ui.Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                header,
                Ui.Code(app.ExePath, Ui.Muted),
                Ui.Text(app.Signer is null ? "Unsigned" : "Signed by " + app.Signer, app.Signer is null ? Ui.Muted : Ui.Ok, 12),
                scopes,
                Ui.Text(when, Ui.Muted, 11.5),
            },
        });
    }
}

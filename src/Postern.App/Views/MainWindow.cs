using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Postern.App.Services;
using Postern.Broker;

namespace Postern.App.Views;

/// <summary>
/// Everything Postern shows apart from the approval prompt, in one window: a nav rail on the left (the same shape as
/// Perch's settings) and one page at a time on the right. The approval prompt stays its own topmost window so it
/// can't be mistaken for, or buried under, this one.
/// </summary>
internal sealed class MainWindow : Window
{
    public const string AppsKey = "apps";
    public const string ConnectionsKey = "connections";
    public const string AuditKey = "audit";
    public const string GeneralKey = "general";
    public const string UpdatesKey = "updates";
    public const string ChangelogKey = "changelog";
    public const string HowKey = "how";

    const double NavWidth = 190;

    readonly Updater _updater;
    readonly AppsPage _apps;
    readonly ConnectionsPage _connections;
    readonly AuditPage _audit;
    readonly UpdatesPage _updates;
    readonly HowItWorksPage _how;
    readonly Panel _content = new();
    readonly Dictionary<string, Control> _pages = [];
    readonly List<(string Key, Button Item, TextBlock Badge)> _nav = [];

    public MainWindow(BrokerRuntime broker, Updater updater, Action shutDownForUpdate, string page = AppsKey)
    {
        _updater = updater;
        Title = $"Postern{Profile.DisplaySuffix}";
        Width = 1240;
        Height = 720;
        MinWidth = 880;
        MinHeight = 520;
        Background = Ui.Background;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = TrayArt.WindowIcon();

        _apps = new AppsPage(broker);
        _connections = new ConnectionsPage(broker);
        _audit = new AuditPage(broker);
        _updates = new UpdatesPage(updater, shutDownForUpdate);
        _how = new HowItWorksPage(broker);

        var nav = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        AddPage(nav, AppsKey, "Apps", _apps);
        AddPage(nav, ConnectionsKey, "Connections", _connections);
        AddPage(nav, AuditKey, "Audit log", _audit);
        AddPage(nav, GeneralKey, "General", new GeneralPage());
        AddPage(nav, UpdatesKey, "Updates", _updates);
        AddPage(nav, ChangelogKey, "Changelog", new ChangelogPage());
        AddPage(nav, HowKey, "How it works", _how);

        var rail = new Border
        {
            Width = NavWidth,
            Background = Ui.Sunken,
            BorderBrush = Ui.Border,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new DockPanel
            {
                Children =
                {
                    Ui.Top(Brand()),
                    new ScrollViewer { Content = nav, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                },
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(rail, 0);
        Grid.SetColumn(_content, 1);
        grid.Children.Add(rail);
        grid.Children.Add(_content);
        Content = grid;

        _connections.Changed += UpdateBadges;
        broker.Registrations.Changed += OnRegistrationsChanged;
        updater.AvailableChanged += OnAvailableChanged;
        Closed += (_, _) =>
        {
            broker.Registrations.Changed -= OnRegistrationsChanged;
            updater.AvailableChanged -= OnAvailableChanged;
        };

        UpdateBadges();
        NavigateTo(page);
    }

    /// <summary>For snapshots, which put these pages into each of their states by hand.</summary>
    public UpdatesPage Updates => _updates;

    /// <inheritdoc cref="Updates"/>
    public HowItWorksPage How => _how;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    public void NavigateTo(string key)
    {
        if (!_pages.ContainsKey(key))
            return;
        foreach (var (pageKey, page) in _pages)
            page.IsVisible = pageKey == key;
        foreach (var (itemKey, item, _) in _nav)
        {
            var selected = itemKey == key;
            item.Background = selected ? Ui.Panel : Ui.Sunken;
            item.Foreground = selected ? Ui.Foreground : Ui.Muted;
            item.BorderBrush = selected ? Ui.Accent : Brushes.Transparent;
        }

        // Audit and credentials change behind the window's back (requests, postern-cli), so reread them on each visit.
        switch (key)
        {
            case AuditKey: _audit.Refresh(); break;
            case ConnectionsKey: _connections.Refresh(); break;
            case UpdatesKey: _updates.Shown(); break;
            case HowKey: _how.Refresh(); break;
        }
    }

    void AddPage(StackPanel nav, string key, string title, Control page)
    {
        page.IsVisible = false;
        _pages[key] = page;
        _content.Children.Add(page);

        var badge = new TextBlock { FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        DockPanel.SetDock(badge, Dock.Right);
        var item = new Button
        {
            Content = new DockPanel { Children = { badge, new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center } } },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Ui.Sunken,
            Foreground = Ui.Muted,
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(15, 0, 12, 0),
            Height = 42,
            FontSize = 14,
        };
        item.Click += (_, _) => NavigateTo(key);
        nav.Children.Add(item);
        _nav.Add((key, item, badge));
    }

    static Control Brand() => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10,
        Margin = new Thickness(16, 18, 12, 14),
        Children =
        {
            Logo(),
            new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "Postern", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ui.Foreground },
                    Ui.Text($"{BrokerRuntime.Version}{Profile.DisplaySuffix}", Ui.Muted, 11),
                },
            },
        },
    };

    static Image Logo()
    {
        var logo = new Image { Source = TrayArt.Logo(), Width = 30, Height = 30 };
        // 256 px drawn at 30: the default interpolation drops pixels and leaves the edges jagged.
        RenderOptions.SetBitmapInterpolationMode(logo, BitmapInterpolationMode.HighQuality);
        return logo;
    }

    void OnRegistrationsChanged() => Dispatcher.UIThread.Post(_apps.Refresh);

    void OnAvailableChanged()
    {
        _updates.AvailableChanged();
        UpdateBadges();
    }

    void UpdateBadges()
    {
        SetBadge(ConnectionsKey, _connections.Missing > 0 ? "missing" : null, Ui.Warning);
        SetBadge(UpdatesKey, _updater.Available is null ? null : "new", Ui.Accent);
    }

    void SetBadge(string key, string? text, IBrush colour)
    {
        foreach (var (itemKey, _, badge) in _nav)
        {
            if (itemKey != key)
                continue;
            badge.Text = text;
            badge.Foreground = colour;
            badge.IsVisible = text is not null;
        }
    }
}

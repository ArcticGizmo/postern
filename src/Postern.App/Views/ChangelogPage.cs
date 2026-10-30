using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Postern.App.Services;
using Postern.Core.Changelog;

namespace Postern.App.Views;

/// <summary>Every release's changes, from the changelog built into this copy, and whether an update shows its own.</summary>
internal sealed class ChangelogPage : UserControl
{
    public ChangelogPage()
    {
        var dataDir = Program.Args.DataDirectory;
        var show = new CheckBox
        {
            Content = "Show what's new after Postern updates",
            Foreground = Ui.Foreground,
            IsChecked = AppSettings.Load(dataDir).ShowChangelogAfterUpdate,
        };
        var error = Ui.Text("", Ui.Warning, 12);
        error.IsVisible = false;
        show.IsCheckedChanged += (_, _) =>
        {
            try
            {
                var settings = AppSettings.Load(dataDir);
                settings.ShowChangelogAfterUpdate = show.IsChecked == true;
                settings.Save(dataDir);
                error.IsVisible = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.Text = "Couldn't save it: " + ex.Message;
                error.IsVisible = true;
            }
        };

        var body = new StackPanel { Margin = new Thickness(18), Spacing = 6, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(Ui.Heading("Changelog"));
        body.Children.Add(Ui.Card(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                show,
                Ui.Text("The first time Postern starts after an update, a window lists the changes since the version you had.", Ui.Muted, 12),
                error,
            },
        }));

        if (ChangelogMarkdown.LoadEmbedded() is { } markdown)
        {
            // An empty [Unreleased] heading means nothing to anyone running a release.
            foreach (var section in ChangelogParser.Parse(markdown).Where(s => s.Version is not null || s.Block.Count > 1))
                ChangelogMarkdown.Render(body, section.Block);
        }
        else
        {
            body.Children.Add(Ui.Text("This build has no changelog in it.", Ui.Muted));
        }

        Content = new ScrollViewer { Content = body };
    }
}

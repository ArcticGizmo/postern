using Avalonia.Threading;
using Postern.App.Views;
using Postern.Core.Apps;

namespace Postern.App.Services;

/// <summary>
/// Queues requests into one <see cref="ApprovalWindow"/>, on the UI thread, opening it when the first arrives.
/// Cancellation (the broker's timeout, or the app asking again) takes the request out of the queue as a deny.
/// </summary>
internal sealed class AvaloniaApprovalPrompt : IApprovalPrompt
{
    ApprovalWindow? _window;
    int _pending;

    /// <summary>Raised on the UI thread when the number of waiting requests changes.</summary>
    public event Action<int>? PendingChanged;

    /// <summary>What a connection name in a scope points at ("GitHub · api.github.com"), or null when there's no such connection.</summary>
    public Func<string, string?>? DescribeConnection { get; set; }

    public async Task<bool> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var item = new PendingApproval(request);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = _window is { IsDone: false } open ? open : NewWindow();
            window.Enqueue(item);
            cancellationToken.Register(() => Dispatcher.UIThread.Post(() => window.Withdraw(item)));
            PendingChanged?.Invoke(++_pending);
            if (!window.IsVisible)
                window.Show();
        });

        try
        {
            return await item.Answer.Task;
        }
        finally
        {
            Dispatcher.UIThread.Post(() => PendingChanged?.Invoke(--_pending));
        }
    }

    ApprovalWindow NewWindow()
    {
        var window = new ApprovalWindow(DescribeConnection);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
                _window = null;
        };
        _window = window;
        return window;
    }
}

using Avalonia.Threading;
using Postern.App.Views;
using Postern.Core.Apps;

namespace Postern.App.Services;

/// <summary>Shows an <see cref="ApprovalWindow"/> per request, on the UI thread. Cancellation (the broker's timeout) closes it as a deny.</summary>
internal sealed class AvaloniaApprovalPrompt : IApprovalPrompt
{
    int _pending;

    /// <summary>Raised on the UI thread when the number of open prompts changes.</summary>
    public event Action<int>? PendingChanged;

    public async Task<bool> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = new ApprovalWindow(request, answer);
            cancellationToken.Register(() => Dispatcher.UIThread.Post(window.TimeOut));
            PendingChanged?.Invoke(++_pending);
            window.Show();
        });

        try
        {
            return await answer.Task;
        }
        finally
        {
            Dispatcher.UIThread.Post(() => PendingChanged?.Invoke(--_pending));
        }
    }
}

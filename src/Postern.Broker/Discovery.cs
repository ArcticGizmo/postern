using Microsoft.Win32;

namespace Postern.Broker;

/// <summary>
/// HKCU\Software\Postern\Pipes\{pipe} → BrokerPath, ProcessId. Clients read BrokerPath and refuse to talk to a pipe
/// server running any other exe. Only this user can write HKCU, so another user can't redirect them; a process
/// running as this user could, but that's outside the trust model.
/// </summary>
public static class Discovery
{
    const string Root = @"Software\Postern\Pipes";

    public static void Publish(string pipeName)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{Root}\{pipeName}");
        key.SetValue("BrokerPath", Environment.ProcessPath ?? "");
        key.SetValue("ProcessId", Environment.ProcessId);
        key.SetValue("StartedAt", DateTimeOffset.UtcNow.ToString("O"));
    }

    public static void Withdraw(string pipeName)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{Root}\{pipeName}", writable: true);
        if (key?.GetValue("ProcessId") is int pid && pid == Environment.ProcessId)
            Registry.CurrentUser.DeleteSubKeyTree($@"{Root}\{pipeName}", throwOnMissingSubKey: false);
    }
}

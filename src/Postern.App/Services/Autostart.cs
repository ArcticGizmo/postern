using Microsoft.Win32;

namespace Postern.App.Services;

/// <summary>
/// Launch at login through the per-user Run key (no admin, no scheduled task), the same way Shrike and Perch do it.
/// The entry starts the supervisor, which keeps Postern running. Off until the user turns it on.
/// </summary>
internal static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Its own entry for a dev build, so ticking Start at login there can't repoint the installed Postern's.
    static readonly string ValueName = "Postern" + Broker.Profile.DisplaySuffix;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(bool enabled, AppArgs args)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var extra = args.PipeName == Broker.Profile.PipeName ? "" : $" --pipe {args.PipeName}";
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --supervise{extra}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>For uninstall, where there are no parsed arguments: whatever the entry says, it's about to point at nothing.</summary>
    public static void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

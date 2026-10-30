using System.Diagnostics;
using Microsoft.Win32;

namespace Postern.Sallyport;

/// <summary>
/// Which Postern Sallyport talks to, and the guard on it. Sallyport sends deletes, uploads and faults, so it only ever
/// talks to a sandbox: postern-sandbox by default, or a throwaway pipe named with --pipe. It refuses the pipes of
/// Postern and Postern (Dev), which hold real credentials, however it's started.
/// </summary>
public static class Target
{
    public const string SandboxPipe = "postern-sandbox";
    static readonly string[] RealPipes = ["postern", "postern-dev"];

    /// <summary>The pipe to use, or throws <see cref="ArgumentException"/> when it's one Sallyport won't touch.</summary>
    public static string Pipe(string? given)
    {
        var pipe = given ?? (Environment.GetEnvironmentVariable("POSTERN_PIPE") is { Length: > 0 } fromEnvironment ? fromEnvironment : SandboxPipe);
        if (RealPipes.Contains(pipe, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Sallyport won't talk to \\\\.\\pipe\\{pipe}: that Postern holds real credentials. Start Postern with --sandbox and use {SandboxPipe}.");
        return pipe;
    }

    /// <summary>Where the Postern on <paramref name="pipe"/> says its exe is (it publishes this for clients to check).</summary>
    public static string? BrokerPath(string pipe)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Postern\Pipes\{pipe}");
        return key?.GetValue("BrokerPath") as string;
    }

    /// <summary>
    /// Kills the Postern serving <paramref name="pipe"/>, for the crash drill. Only after checking the pid really is
    /// that Postern's exe, so a stale or reused pid can't take anything else down. Returns what happened.
    /// </summary>
    public static string Crash(string pipe, int pid)
    {
        if (RealPipes.Contains(pipe, StringComparer.OrdinalIgnoreCase))
            return "Refused: that isn't a sandbox.";
        var expected = BrokerPath(pipe);
        try
        {
            using var process = Process.GetProcessById(pid);
            var path = process.MainModule?.FileName;
            if (expected is null || path is null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
                return $"Refused: pid {pid} is {path ?? "unknown"}, not the Postern serving {pipe} ({expected ?? "unpublished"}).";
            process.Kill();
            return $"Killed pid {pid}.";
        }
        catch (ArgumentException)
        {
            return $"pid {pid} isn't running.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"Couldn't kill pid {pid}: {ex.Message}";
        }
    }

    /// <summary>Starts the sandbox Postern again, from where it last published itself, under its supervisor.</summary>
    public static string Start(string pipe)
    {
        if (pipe != SandboxPipe)
            return $"Only the sandbox can be started from here; start the Postern for {pipe} yourself.";
        if (BrokerPath(pipe) is not { } exe || !File.Exists(exe))
            return "Postern (Sandbox) hasn't run on this machine yet, so there's nothing to start. Run testbed.cmd.";
        // Shell-executed, so it isn't Sallyport's child: it carries on if Sallyport closes.
        using var _ = Process.Start(new ProcessStartInfo(exe, "--sandbox --supervise") { UseShellExecute = true });
        return $"Started {Path.GetFileName(exe)} --sandbox --supervise.";
    }
}

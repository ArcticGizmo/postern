using System.Diagnostics;
using Postern.Broker;

namespace Postern.App.Services;

/// <summary>
/// The test bed's other processes, for the Testbed page: Sallyport and fake-upstream, found in the source tree a Dev or
/// Sandbox build runs from. Each is started as its own process, not a child of Postern, so crashing Postern (the
/// point of the drills) leaves them running.
/// </summary>
internal static class Testbed
{
    public const string UpstreamProbe = "_knobs";

    public static string? RepoRoot { get; } = FindRepoRoot(AppContext.BaseDirectory);

    public static string? SallyportExe => Find(Path.Combine("tools", "Postern.Sallyport", "bin"), "net10.0-windows", "sallyport.exe");

    public static string? FakeUpstreamExe => Find(Path.Combine("tools", "Postern.FakeUpstream", "bin"), "net10.0", "fake-upstream.exe");

    public static Uri UpstreamUrl { get; } = new(Sandbox.UpstreamOrigin + "/");

    /// <summary>Pids of <paramref name="exe"/> that are running from that exact path.</summary>
    public static IReadOnlyList<int> Running(string? exe)
    {
        if (exe is null)
            return [];
        var pids = new List<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                        pids.Add(process.Id);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Exited, or not ours to inspect.
                }
            }
        }
        return pids;
    }

    /// <summary>Shell-executed, so it isn't Postern's child and doesn't share its console.</summary>
    public static void Open(string exe, bool minimised = false) =>
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe),
            WindowStyle = minimised ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal,
        })?.Dispose();

    public static int Stop(string? exe)
    {
        var stopped = 0;
        foreach (var pid in Running(exe))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
                stopped++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
        return stopped;
    }

    public static async Task<bool> UpstreamAnsweringAsync()
    {
        using var http = new HttpClient { BaseAddress = UpstreamUrl, Timeout = TimeSpan.FromSeconds(1) };
        try
        {
            using var response = await http.GetAsync(UpstreamProbe);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public static bool SandboxRunning() => Directory.EnumerateFiles(@"\\.\pipe\", Profile.SandboxPipeName).Any();

    /// <summary>
    /// Starts this exe as Postern (Sandbox), under its supervisor so the crash drill has something to bring it back.
    /// With <paramref name="reset"/> (from the sandbox itself, which then quits) the new one waits for this one to go,
    /// then starts over from a fresh sandbox.
    /// </summary>
    public static void StartSandbox(bool reset = false)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add(AppArgs.SandboxFlag);
        start.ArgumentList.Add("--supervise");
        if (reset)
            start.ArgumentList.Add(AppArgs.ResetSandboxFlag);
        // It gets a supervisor of its own; this one's mustn't carry over.
        start.Environment.Remove(Program.SupervisedVariable);
        start.Environment[Profile.SandboxVariable] = "1";
        Process.Start(start)?.Dispose();
    }

    static string? Find(string bin, string framework, string exe) =>
        RepoRoot is null ? null : new[] { "Debug", "Release" }.Select(c => Path.Combine(RepoRoot, bin, c, framework, exe)).FirstOrDefault(File.Exists);

    static string? FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "postern.slnx")))
                return dir.FullName;
        return null;
    }
}

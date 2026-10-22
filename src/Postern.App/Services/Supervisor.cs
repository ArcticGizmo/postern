using System.Diagnostics;

namespace Postern.App.Services;

/// <summary>
/// <c>Postern.exe --supervise</c>: a tiny watchdog that runs the real Postern as a child and restarts it if it dies.
/// It's what the login entry launches. Chosen over a scheduled task with restart-on-failure because creating
/// scheduled tasks is a classic persistence signal for EDR, and Task Scheduler's minimum restart interval is 1 minute.
/// <list type="bullet">
/// <item>Exit code 0 (Quit from the tray), <see cref="Program.ExitPipeInUse"/> (another Postern owns the pipe) or
/// <see cref="Program.ExitUpdating"/> (an update is about to replace the files): stop.</item>
/// <item>Anything else: restart after 1, 2, 5, 10 s… If it crashes 5 times within 2 minutes, give up and log it.</item>
/// </list>
/// </summary>
internal static class Supervisor
{
    static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
    static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(2);
    const int CrashBudget = 5;

    public static int Run(AppArgs args, Action<string> log)
    {
        using var single = new Mutex(initiallyOwned: true, $@"Local\Postern-Supervisor-{args.PipeName}", out var first);
        if (!first)
        {
            log("Another supervisor is already running for this pipe; exiting.");
            return 0;
        }

        var crashes = new Queue<DateTimeOffset>();
        while (true)
        {
            var started = DateTimeOffset.UtcNow;
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var a in args.ForChild())
                psi.ArgumentList.Add(a);
            psi.Environment[Program.SupervisedVariable] = "1";

            using var child = Process.Start(psi)!;
            log($"Supervisor started Postern pid {child.Id}.");
            child.WaitForExit();
            var code = child.ExitCode;

            // An update needs every process out of the install folder; Velopack restarts the supervisor afterwards.
            if (code is 0 or Program.ExitPipeInUse or Program.ExitUpdating)
            {
                log($"Postern exited with {code}; supervisor stopping.");
                return code;
            }

            var now = DateTimeOffset.UtcNow;
            crashes.Enqueue(now);
            while (crashes.Count > 0 && now - crashes.Peek() > CrashWindow)
                crashes.Dequeue();
            if (crashes.Count >= CrashBudget)
            {
                log($"Postern crashed {crashes.Count} times in {CrashWindow.TotalMinutes:F0} minutes (last exit {code}); giving up.");
                return code;
            }

            var delay = Backoff[Math.Min(crashes.Count - 1, Backoff.Length - 1)];
            log($"Postern pid {child.Id} exited with {code} after {(now - started).TotalSeconds:F0} s; restarting in {delay.TotalSeconds:F0} s.");
            Thread.Sleep(delay);
        }
    }
}

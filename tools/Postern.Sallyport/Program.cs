using System.Runtime.InteropServices;
using Avalonia;
using Postern.Client;

namespace Postern.Sallyport;

/// <summary>
/// <code>
/// sallyport [--pipe NAME] [--upstream URL] [--name NAME] [--run-all]
/// </code>
/// The pipe defaults to %POSTERN_PIPE%, else postern-sandbox; Postern's and Postern (Dev)'s pipes are refused.
/// --run-all skips the window: it runs every scenario, prints a line each, and exits 0 when all of them pass.
/// </summary>
internal sealed record Settings(string Pipe, Uri Upstream, string AppName, bool RunAll, string? Problem);

internal static class Program
{
    public const int ExitOk = 0, ExitFailed = 1, ExitUsage = 2, ExitUnavailable = 3;

    public static Settings Settings { get; private set; } = null!;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Settings = Parse(args);
        }
        catch (ArgumentException ex)
        {
            AttachParentConsole();
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("usage: sallyport [--pipe NAME] [--upstream URL] [--name NAME] [--run-all]");
            return ExitUsage;
        }

        if (Settings.RunAll)
        {
            AttachParentConsole();
            if (Settings.Problem is { } problem)
            {
                Console.Error.WriteLine(problem);
                return ExitUsage;
            }
            return Headless.RunAsync(Settings).GetAwaiter().GetResult();
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
    }

    static Settings Parse(string[] args)
    {
        string? pipe = null, upstream = null, name = null;
        var runAll = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Length: pipe = args[++i]; break;
                case "--upstream" when i + 1 < args.Length: upstream = args[++i]; break;
                case "--name" when i + 1 < args.Length: name = args[++i]; break;
                case "--run-all": runAll = true; break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        var upstreamUrl = new Uri(upstream ?? "http://127.0.0.1:5599/");
        if (!upstreamUrl.IsLoopback)
            throw new ArgumentException("--upstream must be on this machine (127.0.0.1 or localhost).");
        string? problem = null;
        string resolved;
        try
        {
            resolved = Target.Pipe(pipe);
        }
        catch (ArgumentException ex)
        {
            // The window still opens, to say why it won't do anything.
            problem = ex.Message;
            resolved = Target.SandboxPipe;
        }
        return new Settings(resolved, upstreamUrl, name ?? "sallyport", runAll, problem);
    }

    public static PosternClient CreateClient(Settings settings) =>
        new(new PosternOptions { AppName = settings.AppName, PipeName = settings.Pipe });

    // WinExe has no console; when run from a terminal (--run-all, or a usage error) borrow the parent's for output.
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AttachConsole(int processId);

    static void AttachParentConsole()
    {
        if (!AttachConsole(-1))
            return;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

using System.Runtime.InteropServices;
using Avalonia;
using Microsoft.Extensions.Logging;
using Postern.App.Services;
using Postern.Broker;
using Postern.Broker.Pipe;
using Postern.Core.Apps;
using Velopack;

namespace Postern.App;

internal static class Program
{
    public const int ExitBadArguments = 2;
    public const int ExitPipeInUse = 3;
    public const int ExitBrokerFailed = 4;
    public const int ExitElevated = 5;
    public const int ExitUpdating = 6;

    /// <summary>Set by <see cref="Supervisor"/> on its child, so an update restart can bring the watchdog back too.</summary>
    public const string SupervisedVariable = "POSTERN_SUPERVISED";

    /// <summary>Set before Avalonia starts; the App reads it.</summary>
    public static AppArgs Args { get; internal set; } = null!;
    public static FileLoggerProvider Logs { get; internal set; } = null!;

    // Initialization code. Don't use any Avalonia, third-party APIs or any SynchronizationContext-
    // reliant code before AppMain is called: things aren't initialized yet and stuff will break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Before anything reads the profile. As a variable, it also makes everything Postern starts (the supervised
        // child, Sallyport, testbed-upstream, postern-cli) the sandbox too.
        if (args.Contains(AppArgs.SandboxFlag))
            Environment.SetEnvironmentVariable(Profile.SandboxVariable, "1");

        // Before argument parsing: Velopack runs its install/update/uninstall hooks by launching the exe with
        // its own arguments, and exits here when it does.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Updater.CleanUpBeforeUninstall())
            .Run();

        try
        {
            Args = AppArgs.Parse(args);
        }
        catch (ArgumentException ex)
        {
            AttachParentConsole();
            Console.Error.WriteLine(ex.Message);
            return ExitBadArguments;
        }

        var console = (Args.Headless || Args.Supervise) && AttachParentConsole();

        // Before the log file too: an elevated run would leave Administrators-owned files in the data folder.
        if (Elevation.IsElevatedByUac())
        {
            if (console)
                Console.Error.WriteLine(Elevation.Message);
            else
                Elevation.ShowRefusal();
            return ExitElevated;
        }

        Logs = new FileLoggerProvider(Path.Combine(Args.DataDirectory, "logs"), console);
        var log = Logs.CreateLogger("Postern.App");

        if (Args.Supervise)
            return Supervisor.Run(Args, message => Logs.CreateLogger("Postern.Supervisor").LogInformation("{Message}", message));

        using var single = new Mutex(initiallyOwned: true, $@"Local\Postern-{Args.PipeName}", out var first);
        if (!first)
        {
            log.LogInformation("Postern is already running for pipe {Pipe}; exiting.", Args.PipeName);
            return 0;
        }

        // Holding the mutex, so no other Postern on this pipe has the database open.
        if (Args.ResetSandbox)
        {
            try
            {
                Sandbox.Reset(Args.DataDirectory);
                log.LogWarning("Sandbox reset: deleted its database, providers.json and credentials in {Dir}.", Args.DataDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                log.LogError(ex, "Sandbox reset failed");
                return ExitBrokerFailed;
            }
        }

        if (Args.Headless)
            return RunHeadless(log).GetAwaiter().GetResult();

        // The shutdown code is what the supervisor reads: 0 (Quit) and ExitUpdating stop it, anything else restarts Postern.
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
    }

    /// <summary>The broker without a tray. Nobody can approve, so new registrations are denied (except dev auto-approve).</summary>
    static async Task<int> RunHeadless(ILogger log)
    {
        var runtime = CreateBroker(WithDevAutoApprove(new DenyAllPrompt(), log));
        try
        {
            await runtime.StartAsync();
        }
        catch (PipeInUseException ex)
        {
            log.LogError("{Message}", ex.Message);
            return ExitPipeInUse;
        }

        log.LogInformation("Headless: no approval UI, so registrations are denied. Ctrl+C to stop.");
        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.TrySetResult();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
        await stop.Task;
        await runtime.DisposeAsync();
        return 0;
    }

    public static BrokerRuntime CreateBroker(IApprovalPrompt prompt)
    {
        // A bad providers.json mustn't stop Postern starting: the app is where it gets fixed. Its connections stay
        // unavailable (fail closed) until then.
        IReadOnlyList<Core.Providers.ProviderDefinition> providers;
        string? loadError = null;
        try
        {
            if (Profile.IsSandbox && Sandbox.Seed(Args.DataDirectory, Profile.CredentialStore()))
                Logs.CreateLogger("Postern.App").LogInformation("New sandbox: seeded its connections and fake credentials, all pointing at {Upstream}.", Sandbox.UpstreamOrigin);
            providers = BrokerOptions.LoadProviders(Args.DataDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            providers = [Core.Providers.ProviderDefinition.GitHub];
            loadError = ex.Message;
        }

        return BrokerRuntime.Create(
            new BrokerOptions
            {
                PipeName = Args.PipeName,
                DataDirectory = Args.DataDirectory,
                Providers = providers,
                ProvidersLoadError = loadError,
                LoopbackOnly = Profile.IsSandbox,
            },
            prompt,
            new BrokerOverrides { Logging = builder => builder.AddProvider(Logs) });
    }

    public static IApprovalPrompt WithDevAutoApprove(IApprovalPrompt prompt, ILogger log)
    {
#if DEBUG
        if (Args.DevAutoApprove.Count > 0)
        {
            log.LogWarning("DEV AUTO-APPROVE is on for: {Names}. Debug builds only.", string.Join(", ", Args.DevAutoApprove));
            return new DevAutoApprovePrompt(Args.DevAutoApprove, prompt, log);
        }
#else
        if (Args.DevAutoApprove.Count > 0)
            log.LogWarning("--dev-auto-approve ignored: this is a Release build.");
#endif
        return prompt;
    }

    // WinExe has no console; when run from a terminal (headless, supervise) borrow the parent's for output.
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AttachConsole(int processId);

    static bool AttachParentConsole()
    {
        if (!AttachConsole(-1))
            return false;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        return true;
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

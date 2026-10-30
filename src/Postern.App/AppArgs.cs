using Postern.Broker;

namespace Postern.App;

/// <summary>
/// Command line. Everything is optional; a plain launch is the tray app on the default pipe.
/// <code>
/// Postern.exe [--headless] [--supervise] [--sandbox [--reset-sandbox]] [--pipe NAME] [--data-dir DIR] [--dev-auto-approve name1,name2]
/// </code>
/// <c>--sandbox</c> is acted on in <see cref="Program.Main"/> before this runs, since it decides the profile.
/// </summary>
internal sealed record AppArgs(
    bool Headless,
    bool Supervise,
    bool ResetSandbox,
    string PipeName,
    string DataDirectory,
    IReadOnlyList<string> DevAutoApprove,
    IReadOnlyList<string> Raw)
{
    public const string SandboxFlag = "--sandbox";
    public const string ResetSandboxFlag = "--reset-sandbox";

    public static AppArgs Parse(string[] args)
    {
        var headless = false;
        var supervise = false;
        var reset = false;
        var pipe = Profile.PipeName;
        var dataDir = Profile.DataDirectory;
        var autoApprove = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--headless": headless = true; break;
                case "--supervise": supervise = true; break;
                case SandboxFlag: break;
                case ResetSandboxFlag: reset = true; break;
                case "--pipe": pipe = args[++i]; break;
                case "--data-dir": dataDir = Path.GetFullPath(args[++i]); break;
                case "--dev-auto-approve": autoApprove.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                // Velopack's own hook arguments. VelopackApp.Run() handles and exits on the ones it knows, and a
                // newer Velopack adding one mustn't stop Postern starting.
                case var velopack when velopack.StartsWith("--veloapp-", StringComparison.Ordinal): break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }
        if (reset && !Profile.IsSandbox)
            throw new ArgumentException($"{ResetSandboxFlag} only works with {SandboxFlag}.");
        return new(headless, supervise, reset, pipe, dataDir, autoApprove, args);
    }

    /// <summary>
    /// The same arguments without --supervise, for the supervised child. A reset is for the first start only: a
    /// restart after a crash keeps what the sandbox has.
    /// </summary>
    public string[] ForChild(bool firstStart) =>
        [.. Raw.Where(a => a != "--supervise" && (firstStart || a != ResetSandboxFlag))];
}

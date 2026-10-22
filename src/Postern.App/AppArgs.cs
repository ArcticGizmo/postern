using Postern.Broker;

namespace Postern.App;

/// <summary>
/// Command line. Everything is optional; a plain launch is the tray app on the default pipe.
/// <code>
/// Postern.exe [--headless] [--supervise] [--pipe NAME] [--data-dir DIR] [--dev-auto-approve name1,name2]
/// </code>
/// </summary>
internal sealed record AppArgs(
    bool Headless,
    bool Supervise,
    string PipeName,
    string DataDirectory,
    IReadOnlyList<string> DevAutoApprove,
    IReadOnlyList<string> Raw)
{
    public static AppArgs Parse(string[] args)
    {
        var headless = false;
        var supervise = false;
        var pipe = Profile.PipeName;
        var dataDir = Profile.DataDirectory;
        var autoApprove = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--headless": headless = true; break;
                case "--supervise": supervise = true; break;
                case "--pipe": pipe = args[++i]; break;
                case "--data-dir": dataDir = Path.GetFullPath(args[++i]); break;
                case "--dev-auto-approve": autoApprove.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                // Velopack's own hook arguments. VelopackApp.Run() handles and exits on the ones it knows, and a
                // newer Velopack adding one mustn't stop Postern starting.
                case var velopack when velopack.StartsWith("--veloapp-", StringComparison.Ordinal): break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }
        return new(headless, supervise, pipe, dataDir, autoApprove, args);
    }

    /// <summary>The same arguments without --supervise, for the supervised child.</summary>
    public string[] ForChild() => [.. Raw.Where(a => a != "--supervise")];
}

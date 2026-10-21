namespace Spike.Broker;

/// <summary>An upstream: base URL plus where its secret lives. Auth is always "Authorization: Bearer" in the spike.</summary>
internal sealed record Provider(string Name, string BaseUrl, string CredentialTarget);

internal sealed class BrokerOptions
{
    public string PipeName { get; private set; } = "postern-spike";
    public bool AllowRemote { get; private set; }
    public string? Command { get; private set; }
    public string? CommandArg { get; private set; }
    public Dictionary<string, Provider> Providers { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["github"] = new("github", "https://api.github.com", TargetFor("github")),
    };

    public static string TargetFor(string provider) => $"postern:{provider}:default";

    // --pipe <name>
    // --provider <name>=<baseUrl>[|<credentialTarget>]   (repeatable; overrides github if named github)
    // --allow-remote                                     (no reject-remote flag, no NETWORK deny ACE: the contrast test)
    // set-credential <provider> | delete-credential <provider>
    public static BrokerOptions Parse(string[] args)
    {
        var options = new BrokerOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipe":
                    options.PipeName = args[++i];
                    break;
                case "--provider":
                    var spec = args[++i];
                    var eq = spec.IndexOf('=');
                    var name = spec[..eq];
                    var rest = spec[(eq + 1)..].Split('|', 2);
                    options.Providers[name] = new(name, rest[0].TrimEnd('/'), rest.Length > 1 ? rest[1] : TargetFor(name));
                    break;
                case "--allow-remote":
                    options.AllowRemote = true;
                    break;
                case "set-credential" or "delete-credential":
                    options.Command = args[i];
                    options.CommandArg = i + 1 < args.Length ? args[++i] : null;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'");
            }
        }
        return options;
    }
}

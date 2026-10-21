using Spike.Probe;
using Spike.Shared;

var command = args.FirstOrDefault() ?? "help";
var report = new Report();

switch (command)
{
    case "s1":
        await S1.Run(report);
        break;
    case "s2":
        await S2.Run(report);
        break;
    case "all":
        await S1.Run(report);
        await S2.Run(report);
        break;
    case "live":
        await Live.Run(report, args.Contains("--show-identity"), Option("--repo"));
        break;
    case "connect":
        await Connect(Option("--pipe") ?? "postern-spike");
        return 0;
    case "sig":
        var signature = Authenticode.Verify(args[1]);
        Console.WriteLine($"{args[1]}: {signature.Status}, trusted={signature.Trusted}, signer={signature.Subject ?? "<none>"}");
        return 0;
    default:
        Console.WriteLine("""
            Spike.Probe s1        pipe transport, identity, ACL, squatting, overhead (local only)
            Spike.Probe s2        forwarding against a local fake upstream (local only)
            Spike.Probe all       s1 then s2
            Spike.Probe live [--repo owner/name] [--show-identity]
                                  real GitHub through the broker; needs: Spike.Broker.exe set-credential github
            Spike.Probe connect [--pipe postern-spike]
                                  one unverified request to a broker you started yourself (for runas / elevated tests)
            Spike.Probe sig <exe> Authenticode check, as the broker does it on callers
            """);
        return 0;
}

report.Summary();
return report.ExitCode;

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static async Task Connect(string pipe)
{
    Console.WriteLine($"As {System.Security.Principal.WindowsIdentity.GetCurrent().Name}, elevated={Environment.IsPrivilegedProcess}, connecting to \\\\.\\pipe\\{pipe}");
    try
    {
        using var http = PipeHttp.Create(pipe);
        Console.WriteLine("CONNECTED. Broker says: " + S1.Redact(await http.GetStringAsync("_spike/whoami")));
    }
    catch (Exception ex)
    {
        var inner = ex.InnerException ?? ex;
        Console.WriteLine($"REFUSED: {inner.GetType().Name}: {inner.Message}");
    }
}

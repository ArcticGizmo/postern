using System.Security.Cryptography;
using Spike.Broker;
using Spike.Shared;
using Yarp.ReverseProxy.Forwarder;

var options = BrokerOptions.Parse(args);

switch (options.Command)
{
    case "set-credential":
        return SetCredential(options.CommandArg ?? "github");
    case "delete-credential":
        var target = BrokerOptions.TargetFor(options.CommandArg ?? "github");
        Console.WriteLine(CredentialStore.Delete(target) ? $"Deleted {target}." : $"No credential at {target}.");
        return 0;
}

var security = PipeAcl.Build(options.AllowRemote);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.WebHost.UseNamedPipes(o =>
{
    o.CurrentUserOnly = false; // replaced by PipeAcl; see there
    o.CreateNamedPipeServerStream = PipeAcl.Factory(security, options.AllowRemote);
});
builder.WebHost.ConfigureKestrel(k => k.ListenNamedPipe(options.PipeName));
builder.Services.AddHttpForwarder();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<RateLimits>();
builder.Services.AddSingleton<Forwarding>();

var app = builder.Build();

// Requests must be addressed to "postern". Anything else arrived down the pipe by accident, typically a client
// whose HttpClient followed a redirect to another host: its ConnectCallback ignores the host and dials the pipe.
app.Use(async (context, next) =>
{
    if (!string.Equals(context.Request.Host.Host, "postern", StringComparison.OrdinalIgnoreCase))
    {
        await PosternErrors.Write(context, 421, "misdirected-host",
            $"Host '{context.Request.Host}' is not postern. Turn off auto-redirect in the client.");
        return;
    }

    // Every request needs a known caller, as the real broker will (fail closed). Cached per connection.
    try
    {
        CallerIdentity.Get(context);
    }
    catch (Exception ex)
    {
        await PosternErrors.Write(context, 403, "caller-unidentified", ex.Message);
        return;
    }
    await next();
});

app.MapGet("/_spike/ping", () => "pong");
app.MapGet("/_spike/whoami", (HttpContext context) => Results.Json(CallerIdentity.Get(context)));
app.MapGet("/_spike/acl", () => PipeAcl.Sddl(security));
app.MapGet("/_spike/slow", async (int ms) =>
{
    await Task.Delay(ms);
    return "ok";
});
app.MapGet("/_spike/stream", async (HttpContext context, int mb) =>
{
    context.Response.ContentType = "application/octet-stream";
    context.Response.ContentLength = mb * 1024L * 1024L;
    var chunk = TestPattern.Chunk();
    for (var i = 0; i < mb * 16; i++)
        await context.Response.Body.WriteAsync(chunk);
});
app.MapPost("/_spike/echo", async (HttpContext context) =>
{
    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var buffer = new byte[81920];
    long total = 0;
    int read;
    while ((read = await context.Request.Body.ReadAsync(buffer)) > 0)
    {
        sha.AppendData(buffer, 0, read);
        total += read;
    }
    return Results.Json(new { bytes = total, sha256 = Convert.ToHexString(sha.GetHashAndReset()) });
});
app.MapGet("/_postern/ratelimits", (RateLimits limits) => Results.Json(limits.All()));
app.Map("/{provider}/{**rest}", (HttpContext context, string provider, Forwarding forwarding) => forwarding.Handle(context, provider));

try
{
    await app.StartAsync();
}
catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
{
    // FirstPipeInstance failed: the name is taken by another broker or by a squatter. Fail closed.
    await Console.Error.WriteLineAsync($"REFUSING TO START: pipe '{options.PipeName}' already exists ({ex.GetType().Name}: {ex.Message})");
    return 3;
}

app.Logger.LogInformation("Listening on \\\\.\\pipe\\{Pipe} (pid {Pid}); remote clients {Remote}",
    options.PipeName, Environment.ProcessId, options.AllowRemote ? "ALLOWED" : "rejected");
foreach (var p in options.Providers.Values)
    app.Logger.LogInformation("Provider {Name} -> {Url} (credential {Target})", p.Name, p.BaseUrl, p.CredentialTarget);

await app.WaitForShutdownAsync();
return 0;

static int SetCredential(string provider)
{
    var target = BrokerOptions.TargetFor(provider);
    Console.Write($"Paste the secret for {target} (input hidden), then Enter: ");
    var secret = Console.IsInputRedirected ? Console.ReadLine() ?? "" : ReadHidden();
    Console.WriteLine();
    if (secret.Length == 0)
    {
        Console.Error.WriteLine("Nothing entered; unchanged.");
        return 1;
    }
    CredentialStore.Write(target, "postern", secret.Trim());
    Console.WriteLine($"Saved to Windows Credential Manager as {target}.");
    return 0;
}

static string ReadHidden()
{
    var chars = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
            return new string([.. chars]);
        if (key.Key == ConsoleKey.Backspace)
        {
            if (chars.Count > 0)
                chars.RemoveAt(chars.Count - 1);
        }
        else if (!char.IsControl(key.KeyChar))
        {
            chars.Add(key.KeyChar);
        }
    }
}

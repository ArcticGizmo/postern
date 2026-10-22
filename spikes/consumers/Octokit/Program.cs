using Octokit;
using Octokit.Internal;
using Postern.Client;

// octokit-spike [--provider github] [--repo owner/name] [--pipe NAME] [--show-identity]
//
// Octokit.NET, unmodified, calling GitHub through Postern. The only Postern-specific parts are the base address
// (http://postern/{provider}/) and the HTTP handler; Octokit's own credentials are anonymous because Postern adds
// the real ones.

var provider = Option("--provider") ?? "github";
var repo = (Option("--repo") ?? "cli/cli").Split('/');
var pipe = Option("--pipe");

using var postern = new PosternClient(new PosternOptions
{
    AppName = "octokit-spike",
    PipeName = pipe ?? new PosternOptions { AppName = "x" }.PipeName,
});

if (!postern.HasToken)
{
    Console.WriteLine("Registering 'octokit-spike' with Postern; approve it in the Postern window...");
    var registration = await postern.RegisterAsync([$"GET {provider}/user", $"GET {provider}/repos/*/*/pulls", $"GET {provider}/repos/*/*/tarball"]);
    Console.WriteLine($"  {registration.Outcome}: {registration.Detail}");
    if (registration.Outcome != RegistrationOutcome.Approved)
        return 3;
}

var connection = new Connection(
    new ProductHeaderValue("postern-octokit-spike"),
    new Uri($"http://postern/{provider}/"),          // GitHubClient(IConnection) skips Octokit's GHES "/api/v3/" fix-up
    new InMemoryCredentialStore(Credentials.Anonymous),
    new HttpClientAdapter(postern.CreateHandler),
    new SimpleJsonSerializer());
var github = new GitHubClient(connection);

try
{
    var me = await github.User.Current();
    Console.WriteLine($"[PASS] User.Current(): {(args.Contains("--show-identity") ? me.Login : Mask(me.Login))}");

    var pulls = await github.PullRequest.GetAllForRepository(repo[0], repo[1], new ApiOptions { PageSize = 5, PageCount = 1 });
    Console.WriteLine($"[PASS] PullRequest.GetAllForRepository({repo[0]}/{repo[1]}): {pulls.Count} PR(s)");

    var info = connection.GetLastApiInfo();
    Console.WriteLine($"[INFO] Octokit's own rate-limit view: {info?.RateLimit?.Remaining}/{info?.RateLimit?.Limit}");

    try
    {
        await github.Repository.Get(repo[0], repo[1]); // not in scope, on purpose
        Console.WriteLine("[FAIL] Repository.Get succeeded but isn't in scope");
        return 1;
    }
    catch (ApiException ex) when (ex.HttpResponse?.Headers.TryGetValue(PosternError.Header, out var code) == true)
    {
        Console.WriteLine($"[PASS] out-of-scope call refused by Postern: {(int)ex.StatusCode} {code}");
    }

    // Archives 302 to a pre-signed URL on another host. Octokit follows redirects itself; through a pipe transport
    // that redirected request still arrives at Postern, addressed to the other host.
    try
    {
        var archive = await github.Repository.Content.GetArchive(repo[0], repo[1], ArchiveFormat.Tarball);
        Console.WriteLine($"[INFO] GetArchive returned {archive.Length} bytes (Octokit didn't follow the redirect through Postern)");
    }
    catch (HttpRequestException ex) when (ex.Message.Contains("only carries requests to http://postern/"))
    {
        Console.WriteLine($"[PASS] GetArchive's redirect refused clearly by Postern.Client: {ex.Message}");
    }
    return 0;
}
catch (ApiException ex)
{
    string? code = null;
    ex.HttpResponse?.Headers.TryGetValue(PosternError.Header, out code);
    Console.WriteLine($"[FAIL] {(int)ex.StatusCode} {code ?? "(from the provider)"}: {ex.Message}");
    if (code is PosternError.UnknownToken or PosternError.TokenSuperseded or PosternError.NoToken)
        postern.ForgetToken();
    return 1;
}

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string Mask(string login) => login.Length <= 2 ? "**" : $"{login[0]}{new string('*', login.Length - 2)}{login[^1]}";

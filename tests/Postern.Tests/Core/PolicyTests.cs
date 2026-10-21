using Postern.Core.Apps;
using Postern.Core.Scopes;

namespace Postern.Tests.Core;

public class PolicyTests
{
    const string Exe = @"C:\Apps\Perch\Perch.exe";
    static readonly CallerInfo Perch = new(100, Exe, false, null);

    readonly InMemoryRegistrationStore _store = new();

    string Register(RegistrationStatus status, string? signer = null, params string[] scopes)
    {
        var token = Secrets.NewToken();
        _store.Save(new AppRegistration
        {
            Id = Secrets.NewId(),
            Name = "perch",
            ExePath = Exe,
            Signer = signer,
            Scopes = [.. (scopes.Length == 0 ? ["GET github/repos/*/*/pulls/**"] : scopes).Select(Scope.Parse)],
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            ClaimHash = Secrets.Hash(Secrets.NewClaim()),
            TokenHash = Secrets.Hash(token),
        });
        return token;
    }

    AccessDecision Eval(string? token, CallerInfo? caller = null, string method = "GET", string path = "/repos/org/perch/pulls") =>
        AccessPolicy.Evaluate(_store, token, caller ?? Perch, method, "github", path);

    [Fact]
    public void AllowsInScope()
    {
        var d = Eval(Register(RegistrationStatus.Approved));
        Assert.True(d.Allowed);
        Assert.Equal("GET github/repos/*/*/pulls/**", d.MatchedScope!.ToString());
    }

    [Fact] public void NoToken() => Assert.Equal("no-token", Eval(null).Code);
    [Fact] public void UnknownToken() => Assert.Equal("unknown-token", Eval(Secrets.NewToken()).Code);
    [Fact] public void Revoked() => Assert.Equal("app-revoked", Eval(Register(RegistrationStatus.Revoked)).Code);
    [Fact] public void Superseded() => Assert.Equal("token-superseded", Eval(Register(RegistrationStatus.Superseded)).Code);
    [Fact] public void Pending() => Assert.Equal("app-not-approved", Eval(Register(RegistrationStatus.Pending)).Code);
    [Fact] public void OutOfScope() => Assert.Equal("scope-denied", Eval(Register(RegistrationStatus.Approved), method: "POST").Code);
    [Fact] public void AmbiguousPath() => Assert.Equal("bad-path", Eval(Register(RegistrationStatus.Approved), path: "/repos/org/perch/pulls/%2F..").Code);

    [Fact]
    public void OtherExeWithStolenToken() =>
        Assert.Equal("caller-mismatch", Eval(Register(RegistrationStatus.Approved), new CallerInfo(200, @"C:\Evil\perch.exe", false, null)).Code);

    [Fact]
    public void ExePathCaseInsensitive() =>
        Assert.True(Eval(Register(RegistrationStatus.Approved), new CallerInfo(200, Exe.ToUpperInvariant(), false, null)).Allowed);

    [Fact]
    public void SignedRegistrationNeedsSameSigner()
    {
        var token = Register(RegistrationStatus.Approved, signer: "CN=Perch");
        Assert.True(Eval(token, new CallerInfo(1, Exe, true, "CN=Perch")).Allowed);
        Assert.Equal("caller-mismatch", Eval(token, new CallerInfo(1, Exe, false, null)).Code);
        Assert.Equal("caller-mismatch", Eval(token, new CallerInfo(1, Exe, true, "CN=Someone else")).Code);
    }
}

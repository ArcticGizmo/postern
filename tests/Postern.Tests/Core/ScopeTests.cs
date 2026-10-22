using Postern.Core.Scopes;

namespace Postern.Tests.Core;

public class ScopeTests
{
    static RequestPath P(string path) => RequestPath.TryParse(path, out var p, out var e) ? p : throw new InvalidOperationException(e);

    [Theory]
    [InlineData("GET github/repos/*/*/pulls/**", "GET", "/repos/org/perch/pulls", true)]
    [InlineData("GET github/repos/*/*/pulls/**", "GET", "/repos/org/perch/pulls/412/files", true)]
    [InlineData("GET github/repos/*/*/pulls/**", "GET", "/repos/org/perch", false)]
    [InlineData("GET github/repos/*/*/pulls/**", "GET", "/repos/org/pulls", false)]
    [InlineData("GET github/repos/*/*/pulls/**", "POST", "/repos/org/perch/pulls", false)]
    [InlineData("GET github/repos/*/*/pulls/**", "HEAD", "/repos/org/perch/pulls", true)]
    [InlineData("GET github/repos/ORG/*/pulls", "GET", "/repos/org/perch/pulls", true)]
    [InlineData("POST github/graphql", "POST", "/graphql", true)]
    [InlineData("POST github/graphql", "POST", "/graphql/", true)]
    [InlineData("POST github/graphql", "POST", "/graphql/x", false)]
    [InlineData("* github/user", "DELETE", "/user", true)]
    [InlineData("GET github/**", "GET", "/", true)]
    [InlineData("GET github/**", "GET", "/anything/at/all", true)]
    [InlineData("GET github/**/runs", "GET", "/runs", true)]
    [InlineData("GET github/**/runs", "GET", "/repos/a/b/actions/runs", true)]
    [InlineData("GET github/**/runs", "GET", "/repos/a/b/actions/runs/1", false)]
    [InlineData("GET github/*", "GET", "/", false)]
    public void Matches(string scope, string method, string path, bool expected) =>
        Assert.Equal(expected, Scope.Parse(scope).Allows(method, "github", P(path)));

    [Fact]
    public void ProviderMustMatchExactly() =>
        Assert.False(Scope.Parse("GET github/**").Allows("GET", "octopus", P("/x")));

    [Theory]
    [InlineData("github/user")]
    [InlineData("GET")]
    [InlineData("GET GitHub/user")]
    [InlineData("GET _postern/register")]
    [InlineData("GET github/pull*")]
    [InlineData("GET github/a//b")]
    [InlineData("GET github/../user")]
    [InlineData("G3T github/user")]
    public void RejectsMalformed(string text) => Assert.False(Scope.TryParse(text, out _, out _));

    [Fact]
    public void RoundTripsToString() => Assert.Equal("GET github/repos/*/*/pulls/**", Scope.Parse("get  github/repos/*/*/pulls/**").ToString());

    [Fact]
    public void TooManyDoubleStarsRejected() => Assert.False(Scope.TryParse("GET github/**/a/**/a/**/a/**", out _, out _));

    [Fact]
    public void WorstCaseDoubleStarsStayFast()
    {
        var scope = Scope.Parse("GET github/**/a/**/a/**/zzz");
        var path = P("/" + string.Join('/', Enumerable.Repeat("a", 200)));
        var started = DateTime.UtcNow;
        Assert.False(scope.Allows("GET", "github", path));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }
}

public class RequestPathTests
{
    [Theory]
    [InlineData("/repos//x", "empty")]
    [InlineData("/repos/./x", "dot")]
    [InlineData("/repos/../x", "dot")]
    [InlineData("/repos/a%2Fb/x", "encoded slash")]
    [InlineData("/repos/a%2fb/x", "encoded slash")]
    [InlineData("/repos/a\\b", "backslash")]
    [InlineData("/repos/a\u0001", "control")]
    public void RejectsAmbiguousPaths(string path, string reason)
    {
        Assert.False(RequestPath.TryParse(path, out _, out var error));
        Assert.Contains(reason, error);
    }

    [Fact]
    public void RootIsZeroSegments()
    {
        Assert.True(RequestPath.TryParse("/", out var path, out _));
        Assert.Empty(path.Segments);
    }
}

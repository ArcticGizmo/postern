using Postern.Broker;
using Postern.Core.Scopes;
using Postern.Sallyport;

namespace Postern.Tests.Sallyport;

/// <summary>Sallyport's scenario files and its guard on which Postern it talks to.</summary>
public class ScenarioTests
{
    [Fact]
    public void EveryScenarioFileLoads()
    {
        var scenarios = Scenarios.Load();
        Assert.NotEmpty(scenarios);
        Assert.All(scenarios, s => Assert.False(string.IsNullOrEmpty(s.Group), s.Id));
    }

    [Fact]
    public void EveryScopeIsOnePosternAccepts()
    {
        foreach (var scope in Registration.ScopesFor(Scenarios.Load()))
            Assert.True(Scope.TryParse(scope, out _, out var error), error);
    }

    [Fact]
    public void ScenariosOnlyUseSandboxConnections()
    {
        var sandbox = Sandbox.SeedConnections.Select(c => c.Name).ToHashSet();
        foreach (var scenario in Scenarios.Load())
        {
            // A scenario about an unknown connection is the one exception, and it must expect exactly that.
            if (scenario.Expect.PosternError == "unknown-provider")
                continue;
            Assert.Contains(scenario.Request.Connection, sandbox);
        }
    }

    [Fact]
    public void RefusedScenariosAskForNoScope()
    {
        foreach (var scenario in Scenarios.Load().Where(s => s.Expect.PosternError is "scope-denied" or "bad-path" or "no-token" or "unknown-provider"))
            Assert.Null(scenario.ScopeNeeded);
    }

    [Theory]
    [InlineData("postern")]
    [InlineData("postern-dev")]
    [InlineData("POSTERN")]
    public void RealPosternPipesAreRefused(string pipe) =>
        Assert.Throws<ArgumentException>(() => Target.Pipe(pipe));

    [Fact]
    public void SandboxPipeIsTheDefault() => Assert.Equal(Target.SandboxPipe, Target.Pipe(null));
}

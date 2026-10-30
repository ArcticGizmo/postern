namespace Postern.Sallyport;

/// <summary>
/// <c>sallyport --run-all</c>: every scenario, one line each, for scripts and CI. Approval still happens in Postern,
/// so for an unattended run start the sandbox with <c>--dev-auto-approve sallyport</c> (Debug builds).
/// </summary>
internal static class Headless
{
    public static async Task<int> RunAsync(Settings settings)
    {
        IReadOnlyList<Scenario> scenarios;
        try
        {
            scenarios = Scenarios.Load();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Console.Error.WriteLine($"sallyport: scenarios can't be read: {ex.Message}");
            return Program.ExitUsage;
        }

        using var postern = Program.CreateClient(settings);
        using var upstream = new Upstream(settings.Upstream);
        if (!await postern.WaitUntilAvailableAsync(TimeSpan.FromSeconds(15)))
        {
            Console.Error.WriteLine($"sallyport: nothing on \\\\.\\pipe\\{settings.Pipe}. Start it with: Postern.exe --sandbox");
            return Program.ExitUnavailable;
        }
        if (!await upstream.IsRunningAsync())
        {
            Console.Error.WriteLine($"sallyport: fake-upstream isn't answering on {settings.Upstream}. Start it with: fake-upstream.exe");
            return Program.ExitUnavailable;
        }

        var scopes = Registration.ScopesFor(scenarios);
        var (state, _) = await Registration.CheckAsync(postern, scopes);
        if (state == RegistrationState.Revoked)
        {
            Console.Error.WriteLine($"sallyport: '{settings.AppName}' is revoked in Postern. Forget it there (or reset the sandbox) to start over.");
            return Program.ExitUnavailable;
        }
        if (state != RegistrationState.Approved)
        {
            Console.WriteLine($"sallyport: asking Postern for {scopes.Count} scopes; approve '{settings.AppName}' in the Postern window.");
            var (outcome, detail) = await Registration.RequestAsync(postern, scopes);
            if (outcome != RegistrationState.Approved)
            {
                Console.Error.WriteLine($"sallyport: not approved: {detail}");
                return Program.ExitUnavailable;
            }
        }

        using var runner = new Runner(postern, upstream);
        var failed = 0;
        foreach (var scenario in scenarios)
        {
            var result = await runner.RunAsync(scenario);
            Console.WriteLine($"  [{(result.Passed ? "PASS" : "FAIL")}] {scenario.Group} / {scenario.Name}  ({result.Outcome}, {result.Elapsed.TotalMilliseconds:N0} ms)");
            if (result.Passed)
                continue;
            failed++;
            foreach (var check in result.Checks.Where(c => !c.Passed))
                Console.WriteLine($"         ✗ {check.What}{(check.Detail is null ? "" : $": {check.Detail}")}");
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? $"sallyport: all {scenarios.Count} passed" : $"sallyport: {failed} of {scenarios.Count} failed");
        return failed == 0 ? Program.ExitOk : Program.ExitFailed;
    }
}

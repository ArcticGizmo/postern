using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Postern.Shadow;

/// <summary>The other side of the comparison: <c>gh api</c>, the way Perch calls GitHub today.</summary>
internal sealed partial class GhCli(string exe)
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>gh.exe from PATH only, resolved to an absolute path so nothing in the working directory can stand in for it.</summary>
    public static GhCli? Find()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir.Trim('"'), "gh.exe");
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                return new GhCli(candidate);
        }
        return null;
    }

    public string Exe => exe;

    public async Task<string> AskAsync(Probe probe, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = probe.Body is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in probe.Path == "graphql" ? new[] { "api", "graphql", "--input", "-" } : new[] { "api", probe.Path })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null)
            return "gh failed to start";
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (probe.Body is not null)
        {
            await process.StandardInput.WriteAsync(probe.Body);
            process.StandardInput.Close();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return cancellationToken.IsCancellationRequested ? "stopped" : $"gh timed out after {Timeout.TotalSeconds:F0} s";
        }

        var output = await stdout;
        if (process.ExitCode == 0)
            return probe.Summarise(output);

        // gh exits 1 for an HTTP error or a GraphQL error. Report it the way the Postern side would see it.
        var error = await stderr;
        if (HttpStatus().Match(error) is { Success: true } status)
            return "HTTP " + status.Groups[1].Value;
        if (output.TrimStart().StartsWith('{'))
            return probe.Summarise(output);
        return "gh failed: " + error.Split('\n', 2)[0].Trim();
    }

    [GeneratedRegex(@"\(HTTP (\d{3})\)")]
    private static partial Regex HttpStatus();
}

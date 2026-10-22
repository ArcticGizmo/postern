using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Spike.Shared;

namespace Spike.Probe;

/// <summary>S1: pipe transport, caller identity, ACL, squatting, overhead. Needs nothing but this machine.</summary>
internal static class S1
{
    public static async Task Run(Report report)
    {
        report.Section("S1 pipe transport and identity");

        using var broker = await BrokerProcess.StartAsync(BrokerProcess.NewPipeName());
        report.Info("broker started", $"pid {broker.Pid} on \\\\.\\pipe\\{broker.Pipe}");
        using var http = PipeHttp.Create(broker.Pipe, expectedServerExe: PipeHttp.BrokerExe);

        await report.Run("server identity", async () =>
        {
            await using var stream = new NamedPipeClientStream(".", broker.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await stream.ConnectAsync(2000);
            var server = PipeHttp.VerifyServer(stream, PipeHttp.BrokerExe);
            report.Check(server.ProcessId == broker.Pid, "server identity", $"client sees server pid {server.ProcessId}, {server.ExePath}, running as you");
        });

        await report.Run("pipe ACL", async () => report.Info("pipe ACL (SDDL)", await http.GetStringAsync("_spike/acl")));

        await report.Run("caller identity", async () =>
        {
            var caller = await http.GetFromJsonAsync<JsonElement>("_spike/whoami");
            var pid = caller.GetProperty("processId").GetUInt32();
            var exe = caller.GetProperty("exePath").GetString();
            report.Check(pid == Environment.ProcessId && string.Equals(exe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase),
                "caller identity", $"broker sees pid {pid}, {exe}");
            report.Info("caller signature", $"{caller.GetProperty("signatureStatus").GetString()} (unsigned dev build expected)");
            report.Info("identity lookup, first connection", $"{caller.GetProperty("lookupMs").GetDouble():F2} ms (PID → exe → user → WinVerifyTrust)");

            using var request = new HttpRequestMessage(HttpMethod.Get, "_spike/whoami");
            request.Headers.ConnectionClose = true;
            using var response = await http.SendAsync(request);
            using var fresh = await http.GetAsync("_spike/whoami"); // new connection after the close above
            var warm = await fresh.Content.ReadFromJsonAsync<JsonElement>();
            report.Info("identity lookup, later connections", $"{warm.GetProperty("lookupMs").GetDouble():F2} ms (signature from cache: {warm.GetProperty("signatureFromCache").GetBoolean()})");
        });

        await report.Run("latency keep-alive", async () =>
        {
            await http.GetStringAsync("_spike/ping");
            var samples = new List<double>();
            for (var i = 0; i < 2000; i++)
            {
                var sw = Stopwatch.StartNew();
                await http.GetStringAsync("_spike/ping");
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }
            report.Info("round trip, reused connection", Percentiles(samples));
        });

        await report.Run("latency new connection", async () =>
        {
            var samples = new List<double>();
            for (var i = 0; i < 200; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "_spike/ping");
                request.Headers.ConnectionClose = true;
                var sw = Stopwatch.StartNew();
                using var response = await http.SendAsync(request);
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }
            report.Info("round trip, new connection each (incl. client server-check + broker caller lookup)", Percentiles(samples));
        });

        await report.Run("concurrency", async () =>
        {
            const int count = 64, delayMs = 250;
            var sw = Stopwatch.StartNew();
            var results = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => http.GetStringAsync($"_spike/slow?ms={delayMs}")));
            var elapsed = sw.Elapsed.TotalMilliseconds;
            report.Check(results.All(r => r == "ok") && elapsed < delayMs * 4, "concurrency",
                $"{count} parallel {delayMs} ms requests took {elapsed:F0} ms (serial would be {count * delayMs} ms)");
        });

        await report.Run("streamed download", async () =>
        {
            const int mb = 100;
            var sw = Stopwatch.StartNew();
            await using var body = await http.GetStreamAsync($"_spike/stream?mb={mb}");
            var hash = await SHA256.HashDataAsync(body);
            var seconds = sw.Elapsed.TotalSeconds;
            report.Check(hash.SequenceEqual(ExpectedPatternHash(mb)), "streamed download", $"{mb} MB byte-exact in {seconds:F2} s ({mb / seconds:F0} MB/s)");
        });

        await report.Run("streamed upload", async () =>
        {
            const int mb = 20;
            var payload = new byte[mb * 1024 * 1024];
            Random.Shared.NextBytes(payload);
            var sw = Stopwatch.StartNew();
            using var response = await http.PostAsync("_spike/echo", new ByteArrayContent(payload));
            var echo = await response.Content.ReadFromJsonAsync<JsonElement>();
            report.Check(echo.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(payload)), "streamed upload",
                $"{mb} MB hashed by broker in {sw.Elapsed.TotalSeconds:F2} s");
        });

        await report.Run("remote (SMB) path", () =>
        {
            var outcome = TryRemote(broker.Pipe);
            if (outcome.StartsWith("denied", StringComparison.Ordinal)) report.Pass("remote (SMB) path refused", outcome);
            else if (outcome.StartsWith("connected", StringComparison.Ordinal)) report.Fail("remote (SMB) path refused", outcome);
            else report.Info("remote (SMB) path inconclusive", outcome);
            return Task.CompletedTask;
        });

        await report.Run("no TCP listener", async () =>
        {
            var listeners = await TcpListeners(broker.Pid);
            report.Check(listeners.Count == 0, "no TCP listener", listeners.Count == 0 ? $"pid {broker.Pid} has no listening TCP sockets" : string.Join("; ", listeners));
        });

        await report.Run("second instance", async () =>
        {
            using var second = await BrokerProcess.StartExpectingExitAsync(broker.Pipe, TimeSpan.FromSeconds(10));
            report.Check(second.HasExited && second.ExitCode != 0, "second broker on the same pipe refuses to start",
                second.HasExited ? $"exit {second.ExitCode}: {LastLine(second.Output)}" : "still running after 10 s");
        });

        await Squatter(report);
        await NetworkDenyContrast(report);
    }

    static async Task Squatter(Report report)
    {
        report.Section("S1 pipe squatting");
        var pipe = BrokerProcess.NewPipeName();

        // An impostor grabs the name first, with the default ACL and room for many instances, so without
        // FirstPipeInstance the real broker would happily share the name and clients would hit either at random.
        await using var impostor = new NamedPipeServerStream(pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        report.Info("impostor", $"Spike.Probe pid {Environment.ProcessId} holds \\\\.\\pipe\\{pipe}");

        await report.Run("broker refuses squatted name", async () =>
        {
            using var broker = await BrokerProcess.StartExpectingExitAsync(pipe, TimeSpan.FromSeconds(10));
            report.Check(broker.HasExited && broker.ExitCode != 0, "broker refuses to start on a squatted name",
                broker.HasExited ? $"exit {broker.ExitCode}: {LastLine(broker.Output)}" : "broker is running alongside the impostor");
        });

        await report.Run("client refuses impostor", async () =>
        {
            var accept = impostor.WaitForConnectionAsync();
            await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000);
            await accept;
            try
            {
                PipeHttp.VerifyServer(client, PipeHttp.BrokerExe);
                report.Fail("client refuses impostor", "verification passed against the impostor");
            }
            catch (ServerVerificationException ex)
            {
                report.Pass("client refuses impostor before sending anything", ex.Message);
            }
        });
    }

    static async Task NetworkDenyContrast(Report report)
    {
        report.Section("S1 contrast: --allow-remote (no PIPE_REJECT_REMOTE_CLIENTS, no NETWORK deny ACE)");
        using var broker = await BrokerProcess.StartAsync(BrokerProcess.NewPipeName(), "--allow-remote");
        report.Info("remote (SMB) path", TryRemote(broker.Pipe));
    }

    /// <summary>\\localhost\pipe\x goes through the SMB redirector, so the connection carries a network logon (NETWORK SID).</summary>
    static string TryRemote(string pipe)
    {
        using var stream = new NamedPipeClientStream("localhost", pipe, PipeDirection.InOut);
        try
        {
            stream.Connect(3000);
            return "connected over \\\\localhost\\pipe";
        }
        catch (UnauthorizedAccessException)
        {
            return "denied (access denied over \\\\localhost\\pipe)";
        }
        catch (TimeoutException)
        {
            return "timed out (is the Server/LanmanServer service off?)";
        }
        catch (IOException ex)
        {
            return $"IOException: {ex.Message}";
        }
    }

    static async Task<List<string>> TcpListeners(int pid)
    {
        var found = new List<string>();
        foreach (var protocol in new[] { "TCP", "TCPv6" })
        {
            var psi = new ProcessStartInfo("netstat.exe", $"-ano -p {protocol}") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var netstat = Process.Start(psi)!;
            var output = await netstat.StandardOutput.ReadToEndAsync();
            await netstat.WaitForExitAsync();
            found.AddRange(output.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Contains("LISTENING", StringComparison.Ordinal) && l.EndsWith(" " + pid, StringComparison.Ordinal)));
        }
        return found;
    }

    static byte[] ExpectedPatternHash(int mb)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = TestPattern.Chunk();
        for (var i = 0; i < mb * 16; i++)
            sha.AppendData(chunk);
        return sha.GetHashAndReset();
    }

    static string Percentiles(List<double> samples)
    {
        samples.Sort();
        double At(double p) => samples[Math.Min(samples.Count - 1, (int)(samples.Count * p))];
        return $"n={samples.Count} p50 {At(0.50):F3} ms, p99 {At(0.99):F3} ms, max {samples[^1]:F3} ms";
    }

    internal static string LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "<no output>";
}

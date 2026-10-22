using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Spike.Probe;

/// <summary>Launches Spike.Broker.exe on a given pipe, waits for it to listen, and kills it on dispose.</summary>
internal sealed class BrokerProcess : IDisposable
{
    readonly Process _process;
    readonly StringBuilder _output = new();

    public string Pipe { get; }
    public int Pid => _process.Id;
    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;

    public string Output
    {
        get
        {
            lock (_output) return _output.ToString();
        }
    }

    BrokerProcess(string pipe, IEnumerable<string> extraArgs)
    {
        Pipe = pipe;
        var psi = new ProcessStartInfo(PipeHttp.BrokerExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipe);
        foreach (var a in extraArgs)
            psi.ArgumentList.Add(a);

        _process = new Process { StartInfo = psi };
        _process.OutputDataReceived += (_, e) => Append(e.Data);
        _process.ErrorDataReceived += (_, e) => Append(e.Data);
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public static string NewPipeName() => "postern-spike-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Starts a broker and waits until its pipe accepts a connection.</summary>
    public static async Task<BrokerProcess> StartAsync(string pipe, params string[] extraArgs)
    {
        var broker = new BrokerProcess(pipe, extraArgs);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (broker.HasExited)
                throw new InvalidOperationException($"broker exited with {broker.ExitCode}:\n{broker.Output}");
            if (broker.Output.Contains("Listening on"))
                return broker;
            await Task.Delay(50);
        }
        broker.Dispose();
        throw new TimeoutException("broker didn't start listening within 15 s");
    }

    /// <summary>Starts a broker that is expected to refuse to start; returns once it has exited (or after the timeout).</summary>
    public static async Task<BrokerProcess> StartExpectingExitAsync(string pipe, TimeSpan timeout, params string[] extraArgs)
    {
        var broker = new BrokerProcess(pipe, extraArgs);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await broker._process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        return broker;
    }

    public static async Task<bool> PipeAcceptsAsync(string pipe)
    {
        await using var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync(200);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    void Append(string? line)
    {
        if (line is null) return;
        lock (_output) _output.AppendLine(line);
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        _process.Dispose();
    }
}

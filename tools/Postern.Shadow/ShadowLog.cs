namespace Postern.Shadow;

/// <summary>Timestamped lines to the console and to %LOCALAPPDATA%\Postern.Shadow\logs\{name}.log, which the stack script reads.</summary>
internal sealed class ShadowLog
{
    readonly string _path;
    readonly Lock _gate = new();

    public ShadowLog(string name)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Postern.Shadow", "logs");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".log");
    }

    public void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (_gate)
        {
            Console.WriteLine(line);
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }
}

namespace Spike.Probe;

/// <summary>PASS / FAIL / INFO lines, and an exit code that fails if any check failed.</summary>
internal sealed class Report
{
    int _failed;
    int _passed;

    public int ExitCode => _failed == 0 ? 0 : 1;

    public void Section(string title)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"== {title}");
        Console.ResetColor();
    }

    public void Pass(string check, string detail = "") => Line("PASS", ConsoleColor.Green, check, detail, ref _passed);
    public void Fail(string check, string detail = "") => Line("FAIL", ConsoleColor.Red, check, detail, ref _failed);

    public void Info(string check, string detail = "")
    {
        var none = 0;
        Line("INFO", ConsoleColor.DarkYellow, check, detail, ref none);
    }

    public void Check(bool ok, string check, string detail = "")
    {
        if (ok) Pass(check, detail);
        else Fail(check, detail);
    }

    public async Task Run(string check, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            Fail(check, $"threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Summary()
    {
        Console.WriteLine();
        Console.ForegroundColor = _failed == 0 ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        Console.ResetColor();
    }

    static void Line(string tag, ConsoleColor colour, string check, string detail, ref int counter)
    {
        counter++;
        Console.ForegroundColor = colour;
        Console.Write($"  [{tag}] ");
        Console.ResetColor();
        Console.WriteLine(Redact(detail.Length == 0 ? check : $"{check}: {detail}"));
    }

    static readonly string ProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string UserSid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;

    /// <summary>Keep your SID and profile path (which carries your name) out of output that might get pasted somewhere.</summary>
    public static string Redact(string text) => text
        .Replace(UserSid, "<your-SID>", StringComparison.Ordinal)
        .Replace(ProfilePath, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
        .Replace(ProfilePath.Replace(@"\", @"\\"), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
}

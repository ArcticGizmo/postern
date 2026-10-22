using Postern.Broker;
using Postern.Broker.Credentials;
using Postern.Core.Providers;
using Postern.Core.Scopes;

namespace Postern.Cli;

/// <summary>
/// <c>postern-cli credential set|delete|list [provider] [--data-dir DIR]</c>: the app's Credentials page for scripts.
/// The secret comes from stdin (piped) or a hidden prompt, never from the command line (which lands in history and
/// process listings). It writes Credential Manager directly, as the tray does; it doesn't go through the pipe, and
/// it's no more privileged than any process running as you (which is the trust model's line anyway).
/// It lives here rather than in Postern.exe because Postern.exe is a WinExe: PowerShell doesn't wait for it, so a
/// hidden prompt there would fight the shell for keystrokes.
/// </summary>
internal static class CredentialCommand
{
    public static int Run(string[] args, Action<string> write, Action<string> error)
    {
        var verb = args.ElementAtOrDefault(0);
        var dataDirIndex = Array.IndexOf(args, "--data-dir");
        var dataDir = dataDirIndex >= 0 && dataDirIndex + 1 < args.Length ? Path.GetFullPath(args[dataDirIndex + 1]) : Profile.DataDirectory;
        var store = Profile.CredentialStore();

        if (verb == "list")
        {
            foreach (var provider in BrokerOptions.LoadProviders(dataDir))
                write($"{provider.Name,-12} {(store.Read(provider.CredentialTarget) is null ? "missing" : "stored"),-8} {provider.BaseUrl}");
            return 0;
        }

        var name = args.ElementAtOrDefault(1);
        if (verb is not ("set" or "delete") || name is null || !ProviderName.IsValid(name))
        {
            error("usage: postern-cli credential set|delete <provider> | credential list   [--data-dir DIR]");
            return 2;
        }

        var target = new ProviderDefinition(name, new Uri("https://unused.invalid")).CredentialTarget;
        var stored = Profile.StoredName(target);
        if (verb == "delete")
        {
            write(store.Delete(target) ? $"Deleted {stored}." : $"Nothing stored at {stored}.");
            return 0;
        }

        string secret;
        if (Console.IsInputRedirected)
        {
            secret = Console.In.ReadToEnd().Trim();
        }
        else
        {
            Console.Write($"Secret for {name} (input hidden): ");
            secret = ReadHidden();
            Console.WriteLine();
        }
        secret = secret.Trim((char)0xFEFF, ' ', '\r', '\n', '\t'); // a PowerShell pipe can prepend a BOM
        if (ProviderDefinition.CheckSecret(secret) is { } problem)
        {
            error($"Not stored: the secret {problem}.");
            return 1;
        }
        store.Write(target, secret);
        write($"Stored as {stored} in Windows Credential Manager.");
        return 0;
    }

    static string ReadHidden()
    {
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                return new string([.. chars]).Trim();
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                    chars.RemoveAt(chars.Count - 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                chars.Add(key.KeyChar);
            }
        }
    }
}

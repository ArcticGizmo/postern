using System.Text.Json;
using Postern.Broker;
using Postern.Broker.Credentials;
using Postern.Core.Providers;
using Postern.Core.Scopes;

namespace Postern.Cli;

/// <summary>
/// <c>postern-cli credential set|delete|list [connection] [--data-dir DIR]</c>: the app's Connections page for scripts.
/// The secret comes from stdin (piped) or a hidden prompt, never from the command line (which lands in history and
/// process listings). A connection with several fields (Jira's email and token) prompts for each, or takes a JSON
/// object of them on stdin. It writes Credential Manager directly, as the tray does; it doesn't go through the pipe,
/// and it's no more privileged than any process running as you (which is the trust model's line anyway).
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

        IReadOnlyList<ProviderDefinition> connections;
        try
        {
            connections = BrokerOptions.LoadProviders(dataDir);
        }
        catch (ArgumentException ex)
        {
            error($"{ProvidersFile.PathIn(dataDir)} can't be used: {ex.Message} Fix it in Postern (Connections → Edit as JSON).");
            return 1;
        }

        if (verb == "list")
        {
            foreach (var connection in connections)
                write($"{connection.Name,-16} {State(store, connection),-8} {connection.TemplateName,-24} {connection.BaseUrl}");
            return 0;
        }

        var name = args.ElementAtOrDefault(1);
        if (verb is not ("set" or "delete") || name is null || !ProviderName.IsValid(name))
        {
            error("usage: postern-cli credential set|delete <connection> | credential list   [--data-dir DIR]");
            return 2;
        }

        var stored = Profile.StoredName(ProviderDefinition.TargetFor(name));
        if (verb == "delete")
        {
            write(store.Delete(ProviderDefinition.TargetFor(name)) ? $"Deleted {stored}." : $"Nothing stored at {stored}.");
            return 0;
        }

        if (connections.FirstOrDefault(c => c.Name == name) is not { } definition)
        {
            error($"There's no connection called '{name}'. Add it in Postern (Connections → Add connection) first.");
            return 1;
        }

        Dictionary<string, string> values;
        try
        {
            values = Console.IsInputRedirected ? FromStdin(definition) : Prompt(definition);
            store.Write(definition.CredentialTarget, definition.EncodeCredential(values));
        }
        catch (ArgumentException ex)
        {
            error($"Not stored: {ex.Message}");
            return 1;
        }
        write($"Stored as {stored} in Windows Credential Manager, for {definition.Origin}.");
        return 0;
    }

    static string State(ICredentialStore store, ProviderDefinition connection) =>
        store.Read(connection.CredentialTarget) is not { } blob ? "missing"
        : connection.DecodeCredential(blob, out _) is null ? "invalid"
        : "stored";

    // One field: the whole of stdin is the secret. Several: a JSON object of them, e.g. {"email":"…","token":"…"}.
    static Dictionary<string, string> FromStdin(ProviderDefinition definition)
    {
        var text = Clean(Console.In.ReadToEnd());
        if (definition.Fields.Count == 1)
            return new() { [definition.Fields[0].Name] = text };
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(text)?.ToDictionary(p => p.Key, p => Clean(p.Value))
                   ?? throw new ArgumentException("stdin was empty.");
        }
        catch (JsonException)
        {
            throw new ArgumentException($"'{definition.Name}' has {definition.Fields.Count} fields, so stdin must be a JSON object: " +
                                        "{" + string.Join(", ", definition.Fields.Select(f => $"\"{f.Name}\": \"…\"")) + "}");
        }
    }

    static Dictionary<string, string> Prompt(ProviderDefinition definition)
    {
        var values = new Dictionary<string, string>();
        foreach (var field in definition.Fields)
        {
            Console.Write(field.Secret ? $"{field.Label} for {definition.Name} (input hidden): " : $"{field.Label} for {definition.Name}: ");
            values[field.Name] = Clean(field.Secret ? ReadHidden() : Console.ReadLine() ?? "");
            if (field.Secret)
                Console.WriteLine();
        }
        return values;
    }

    static string Clean(string text) => text.Trim((char)0xFEFF, ' ', '\r', '\n', '\t'); // a PowerShell pipe can prepend a BOM

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

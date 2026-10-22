using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Postern.Client;

public interface ITokenStore
{
    string? Load();
    void Save(string token);
    void Clear();
}

/// <summary>The token in a file, encrypted with DPAPI for the current user (useless if copied off the machine or to another account).</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiFileTokenStore(string path) : ITokenStore
{
    static readonly byte[] Entropy = "Postern.Client app token v1"u8.ToArray();

    public string Path { get; } = path;

    public static DpapiFileTokenStore ForApp(string appName)
    {
        var safe = string.Concat(appName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
        return new(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Postern.Client", safe + ".token"));
    }

    public string? Load()
    {
        if (!File.Exists(Path))
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null; // another user's file, or a different machine: treat as no token
        }
    }

    public void Save(string token)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllBytes(Path, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
    }

    public void Clear()
    {
        if (File.Exists(Path))
            File.Delete(Path);
    }
}

/// <summary>For tests and short-lived tools.</summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    string? _token;
    public string? Load() => _token;
    public void Save(string token) => _token = token;
    public void Clear() => _token = null;
}

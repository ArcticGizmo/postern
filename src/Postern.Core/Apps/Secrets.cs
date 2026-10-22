using System.Security.Cryptography;
using System.Text;

namespace Postern.Core.Apps;

/// <summary>App tokens and claim codes: 256 random bits, base64url, with a prefix so a leaked one is recognisable.</summary>
public static class Secrets
{
    public const string TokenPrefix = "pst_";
    public const string ClaimPrefix = "psc_";

    public static string NewToken() => TokenPrefix + Random();
    public static string NewClaim() => ClaimPrefix + Random();

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static bool HashEquals(string secret, string expectedHash) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(secret)), Encoding.ASCII.GetBytes(expectedHash));

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    static string Random() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

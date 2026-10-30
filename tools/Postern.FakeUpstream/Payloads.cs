using System.Security.Cryptography;
using System.Text;

namespace Postern.FakeUpstream;

/// <summary>Generated bodies: the same bytes every time for the same inputs, so a client can check what arrived.</summary>
public static class Payloads
{
    public const string ShaHeader = "X-Fake-Body-Sha256";

    const string Mixed = "Sortie ✓ 🏰 שלום עולם 𝄞 naïve café — ";

    public static byte[] UnicodeIssue(int number, int kb)
    {
        var repeats = Math.Max(1, (kb * 1024 - 200) / Encoding.UTF8.GetByteCount(Mixed));
        var json = $$"""{"number":{{number}},"title":"Sortie ✓ 🏰 שלום 𝄞","body":"{{string.Concat(Enumerable.Repeat(Mixed, repeats))}}"}""";
        return Encoding.UTF8.GetBytes(json);
    }

    public static byte[] Binary(string seed, int kb)
    {
        var bytes = new byte[kb * 1024];
        var block = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        for (var i = 0; i < bytes.Length; i += block.Length)
        {
            block = SHA256.HashData(block);
            Array.Copy(block, 0, bytes, i, Math.Min(block.Length, bytes.Length - i));
        }
        return bytes;
    }

    /// <summary>Writes <paramref name="body"/> with its hash in <see cref="ShaHeader"/>.</summary>
    public static IResult Send(HttpContext context, byte[] body, string contentType)
    {
        context.Response.Headers[ShaHeader] = Convert.ToHexStringLower(SHA256.HashData(body));
        return Results.Bytes(body, contentType);
    }
}

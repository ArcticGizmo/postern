namespace Postern.Core.Scopes;

public static class ProviderName
{
    /// <summary>Lowercase letters, digits and '-', 1–32 chars, starting with a letter. Never '_' (reserved for /_postern).</summary>
    public static bool IsValid(string name) =>
        name.Length is > 0 and <= 32
        && name[0] is >= 'a' and <= 'z'
        && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}

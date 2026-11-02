using System.Security.Cryptography;
using System.Text;

namespace SignalGrid.Ampelum.Context;

/// <summary>SHA-256 content hasher producing lowercase hex strings.</summary>
public static class ContentHasher
{
    /// <summary>Returns the lowercase hex SHA-256 of the UTF-8 encoded text.</summary>
    public static string HashHex(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Returns the lowercase hex SHA-256 of the given bytes.</summary>
    public static string HashHex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexStringLower(hash);
    }
}
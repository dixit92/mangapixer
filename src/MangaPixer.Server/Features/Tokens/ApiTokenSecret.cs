namespace com.lifepixer.mangapixer.Server.Features.Tokens;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The format of a personal access token: <c>mpx_</c> + base64url of 32 random bytes (47 characters). The fixed prefix makes a
/// leaked token easy to recognise (secret scanners, log review). Only <see cref="Hash"/> of the whole token is stored: a 256-bit
/// random secret cannot be guessed, so a fast hash is enough (a slow password hash would only cost every request time).
/// </summary>
public static class ApiTokenSecret
{
    /// <summary>The fixed start of every token.</summary>
    public const string Marker = "mpx_";

    /// <summary>Random bytes per token.</summary>
    public const int SecretBytes = 32;

    /// <summary>Total length: the marker plus unpadded base64url of 32 bytes (43 characters).</summary>
    public const int Length = 47;

    /// <summary>Characters of the token kept for display (<c>mpx_</c> + 4), not enough to use or guess it.</summary>
    public const int DisplayPrefixLength = 8;

    /// <summary>A new token, its display prefix and the hash to store.</summary>
    public static (string Token, string Prefix, string Hash) Generate()
    {
        Span<byte> bytes = stackalloc byte[SecretBytes];
        RandomNumberGenerator.Fill(bytes);
        var token = Marker + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (token, token[..DisplayPrefixLength], Hash(token));
    }

    /// <summary>
    /// True when <paramref name="value"/> has the shape of a token (marker, length, base64url characters). Checked before any
    /// hashing or lookup, so arbitrary header values never reach the database.
    /// </summary>
    public static bool IsWellFormed(string? value)
    {
        if (value is null || value.Length != Length || !value.StartsWith(Marker, StringComparison.Ordinal))
            return false;
        for (var i = Marker.Length; i < value.Length; i++)
        {
            var c = value[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                return false;
        }
        return true;
    }

    /// <summary>SHA-256 of the whole token (UTF-8), lowercase hex.</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Constant-time comparison of two hex hashes (the stored and the computed one).</summary>
    public static bool HashesMatch(string stored, string computed) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored), Encoding.ASCII.GetBytes(computed));
}

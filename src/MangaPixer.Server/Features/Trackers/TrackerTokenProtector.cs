namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

/// <summary>
/// Encrypts tracker credentials at rest (1.43.0): access / refresh tokens, the admin's client secret and the PKCE verifier of a sign-in
/// in progress, with the instance's Data Protection key ring (purpose <see cref="Purpose"/>). Honest scope (as audit D16 says for
/// the key ring): this protects every copy of the DATABASE alone - a backup on another share, a DB file sent for support, a restore
/// elsewhere - not a fully copied data root. A payload the key ring cannot open (a DB restored without its keys) is "unreadable":
/// callers turn the account "needs reconnect", never retry.
/// </summary>
public sealed class TrackerTokenProtector
{
    public const string Purpose = "MangaPixer.Trackers.Token.v1";

    private readonly IDataProtector _protector;

    public TrackerTokenProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        return _protector.Protect(plaintext);
    }

    /// <summary>The plaintext, or null when <paramref name="payload"/> is empty or cannot be opened with this key ring.</summary>
    public string? TryUnprotect(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return null;
        try
        {
            return _protector.Unprotect(payload);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

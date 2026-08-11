namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// Encrypts/decrypts short-lived-in-memory secret material (SwitchBot Token/Secret
/// today) before it is written to, or after it is read from, the database. The only
/// production implementation wraps ASP.NET Core Data Protection
/// (<c>DataProtectionCredentialProtector</c> in Infrastructure); there must never be
/// a hand-rolled reversible "encryption" (XOR/Base64/etc.) implementation of this
/// interface. <see cref="Unprotect"/> results must never be logged, thrown in an
/// exception message, or returned from an API/UI once stored.
/// </summary>
public interface ICredentialProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/> into an opaque, storable blob.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts a blob previously produced by <see cref="Protect"/>. Throws
    /// <see cref="System.Security.Cryptography.CryptographicException"/> (never a
    /// generic Exception that could be mistaken for "not configured") if the blob is
    /// corrupt, was protected under a different/rotated purpose, or the key ring is
    /// unavailable.
    /// </summary>
    string Unprotect(string protectedValue);
}

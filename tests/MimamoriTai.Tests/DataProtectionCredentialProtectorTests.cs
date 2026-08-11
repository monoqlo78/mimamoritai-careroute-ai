using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using MimamoriTai.Infrastructure.Security;

namespace MimamoriTai.Tests;

/// <summary>
/// Round-trip and safety tests for the only production <see cref="MimamoriTai.Core.Abstractions.ICredentialProtector"/>
/// implementation. Uses <see cref="EphemeralDataProtectionProvider"/> (in-memory key ring, no disk/registry
/// persistence) so these tests never touch the real machine/app key ring.
/// </summary>
public class DataProtectionCredentialProtectorTests
{
    private static DataProtectionCredentialProtector CreateProtector()
        => new(new EphemeralDataProtectionProvider());

    [Fact]
    public void Protect_Then_Unprotect_RoundTrips_The_Original_Plaintext()
    {
        var protector = CreateProtector();
        const string plaintext = "super-secret-switchbot-token";

        var protectedValue = protector.Protect(plaintext);
        var unprotected = protector.Unprotect(protectedValue);

        Assert.Equal(plaintext, unprotected);
    }

    [Fact]
    public void Protect_Never_Returns_The_Plaintext_Verbatim()
    {
        var protector = CreateProtector();
        const string plaintext = "super-secret-switchbot-secret";

        var protectedValue = protector.Protect(plaintext);

        Assert.NotEqual(plaintext, protectedValue);
        Assert.DoesNotContain(plaintext, protectedValue, StringComparison.Ordinal);
    }

    [Fact]
    public void Protect_Is_Non_Deterministic_Across_Calls_For_The_Same_Plaintext()
    {
        // Data Protection includes randomness/IV material per call; this is a property we
        // rely on elsewhere (it's why the LINE link-code hashing uses a separate HMAC scheme
        // instead of comparing protected blobs). Assert it holds for this protector too.
        var protector = CreateProtector();
        const string plaintext = "same-value-both-times";

        var first = protector.Protect(plaintext);
        var second = protector.Protect(plaintext);

        Assert.NotEqual(first, second);
        Assert.Equal(plaintext, protector.Unprotect(first));
        Assert.Equal(plaintext, protector.Unprotect(second));
    }

    [Fact]
    public void Unprotect_Throws_A_CryptographicException_For_A_Tampered_Or_Foreign_Blob()
    {
        var protector = CreateProtector();

        // Not a value ever produced by this protector -- simulates corruption or a blob
        // encrypted under a different purpose/key ring. Must fail loudly, never silently
        // return garbage or plaintext-looking data.
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("not-a-real-protected-value"));
    }

    [Fact]
    public void Unprotect_Throws_When_Given_A_Blob_Protected_Under_A_Different_Purpose()
    {
        // Two protectors built from the same key ring but different purpose strings must not
        // be able to read each other's blobs -- this is exactly what makes the purpose string
        // a stable "namespace" that must never change without a migration story.
        var keyRing = new EphemeralDataProtectionProvider();
        var switchBotProtector = new DataProtectionCredentialProtector(keyRing);
        var otherPurposeProtector = keyRing.CreateProtector("Some.Other.Purpose.v1");

        var blobFromOtherPurpose = otherPurposeProtector.Protect("irrelevant-value");

        Assert.ThrowsAny<CryptographicException>(() => switchBotProtector.Unprotect(blobFromOtherPurpose));
    }
}

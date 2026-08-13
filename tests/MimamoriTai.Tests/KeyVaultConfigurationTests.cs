using Azure;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using MimamoriTai.Web.Services;

namespace MimamoriTai.Tests;

/// <summary>
/// The Key Vault provider loads synchronously while the host is still being built, so an
/// unreachable vault used to throw straight through Program.Main and abort the process,
/// which App Service turns into a 503 for the whole site. These tests pin down that the
/// vault is now best-effort: it is wired up when configured, skipped when not, and a
/// failure is reported without stopping startup.
/// </summary>
public class KeyVaultConfigurationTests
{
    private static WebApplicationBuilder BuilderWith(string? vaultUri)
    {
        var builder = WebApplication.CreateBuilder();

        if (vaultUri is not null)
        {
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { [KeyVaultConfigurationExtensions.UriKey] = vaultUri });
        }

        return builder;
    }

    [Fact]
    public void Adds_Provider_When_Uri_Is_Configured()
    {
        Uri? seen = null;
        var builder = BuilderWith("https://kv-mimamoritai-hack.vault.azure.net/");

        builder.AddMimamoriTaiKeyVault((_, uri) => seen = uri, _ => Assert.Fail("should not fail"));

        Assert.Equal(new Uri("https://kv-mimamoritai-hack.vault.azure.net/"), seen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-uri")]
    public void Skips_Provider_When_Uri_Is_Missing_Or_Invalid(string? vaultUri)
    {
        var called = false;
        var builder = BuilderWith(vaultUri);

        builder.AddMimamoriTaiKeyVault((_, _) => called = true, _ => Assert.Fail("should not fail"));

        Assert.False(called);
    }

    [Fact]
    public void Startup_Continues_When_Vault_Is_Unreachable()
    {
        string? reported = null;
        var builder = BuilderWith("https://kv-mimamoritai-hack.vault.azure.net/");

        // What the vault actually threw during the outage: the governance policy had flipped
        // publicNetworkAccess to disabled, so the managed identity got a 403 on first load.
        var failure = new RequestFailedException(
            403,
            "Public network access is disabled and request is not from a trusted service nor via an approved private link.");

        var returned = builder.AddMimamoriTaiKeyVault(
            (_, _) => throw failure,
            message => reported = message);

        Assert.Same(builder, returned);
        Assert.NotNull(reported);
        Assert.Contains("kv-mimamoritai-hack", reported);
        Assert.Contains("Continuing without it", reported);
    }

    [Fact]
    public void Startup_Failure_Does_Not_Leave_Vault_Values_Behind()
    {
        var builder = BuilderWith("https://kv-mimamoritai-hack.vault.azure.net/");

        builder.AddMimamoriTaiKeyVault((_, _) => throw new RequestFailedException(403, "denied"), _ => { });

        // Secrets never loaded, so a feature that needs one still sees nothing and stays
        // disabled, rather than silently running on a stale or half-applied value.
        Assert.Null(builder.Configuration["MimamoriTai:SecretThatOnlyExistsInTheVault"]);
    }
}

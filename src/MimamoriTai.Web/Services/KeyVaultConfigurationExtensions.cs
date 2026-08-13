using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace MimamoriTai.Web.Services;

/// <summary>
/// Loads every secret out of Azure Key Vault into the configuration at startup, so
/// no API key, connection string or client secret has to exist as a plain value in
/// App Service settings, in appsettings.json, or anywhere in the repository.
///
/// Authentication is passwordless: <see cref="DefaultAzureCredential"/> resolves to the
/// Web App's system-assigned managed identity in Azure (granted "Key Vault Secrets User")
/// and to the developer's own az login / Visual Studio account locally, so the same code
/// path works in both places without a bootstrap secret.
///
/// Secret names use the standard double-dash convention that the Key Vault configuration
/// provider maps onto the configuration hierarchy: <c>OrcaRouter--ApiKey</c> becomes
/// <c>OrcaRouter:ApiKey</c>. Because this provider is added last it wins over
/// appsettings.json, and because it is registered only when <c>KeyVault:Uri</c> is set,
/// the app still starts with zero configuration (all-mock demo mode) when it is absent.
/// </summary>
public static class KeyVaultConfigurationExtensions
{
    public const string UriKey = "KeyVault:Uri";

    public static WebApplicationBuilder AddMimamoriTaiKeyVault(this WebApplicationBuilder builder)
    {
        var uri = builder.Configuration[UriKey];

        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var vaultUri))
        {
            return builder;
        }

        var client = new SecretClient(vaultUri, new DefaultAzureCredential());

        builder.Configuration.AddAzureKeyVault(
            client,
            new AzureKeyVaultConfigurationOptions
            {
                // Picks up rotated values without a redeploy. Any failure to reload is
                // non-fatal: the previously loaded values stay in effect.
                ReloadInterval = TimeSpan.FromMinutes(30)
            });

        return builder;
    }
}

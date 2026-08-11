using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Devices;

/// <summary>
/// Default <see cref="IHouseholdSwitchBotClientFactory"/>: looks up the household's
/// <see cref="MimamoriTai.Core.Domain.SwitchBotConnection"/> row, decrypts its
/// Token/Secret for the duration of this call only, and builds a brand-new
/// <see cref="SwitchBotClient"/> bound to exactly those credentials. Nothing
/// decrypted here is stored on this factory instance (it has no mutable state), so
/// concurrent calls for different households can never observe each other's secrets.
/// </summary>
public sealed class HouseholdSwitchBotClientFactory(
    IAppDbContext db,
    ICredentialProtector protector,
    IHttpClientFactory httpClientFactory,
    IOptions<SwitchBotOptions> globalOptions,
    ILoggerFactory loggerFactory,
    ILogger<HouseholdSwitchBotClientFactory> logger) : IHouseholdSwitchBotClientFactory
{
    /// <summary>Named HttpClient registered for SwitchBotClient in ServiceCollectionExtensions.</summary>
    public const string HttpClientName = "SwitchBotHousehold";

    public async Task<ISwitchBotClient> GetClientAsync(Guid householdId, CancellationToken ct = default)
    {
        var connection = await db.SwitchBotConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.HouseholdId == householdId, ct);

        if (connection is not null)
        {
            string token;
            string secret;
            try
            {
                // Decrypted only into local variables for the lifetime of this call;
                // never logged, never assigned to a field, never returned as-is.
                token = protector.Unprotect(connection.EncryptedToken);
                secret = protector.Unprotect(connection.EncryptedSecret);
            }
            catch (CryptographicException)
            {
                // Undecryptable blob (e.g. Data Protection key ring rotated/lost).
                // Behave as "not configured" rather than throwing out of a poll loop
                // or blocking an unrelated household's request.
                logger.LogWarning(
                    "SwitchBot credentials for household {HouseholdId} could not be decrypted; treating as not configured.",
                    householdId);
                return NotConfiguredSwitchBotClient.Instance;
            }

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(secret))
            {
                return NotConfiguredSwitchBotClient.Instance;
            }

            var perHousehold = Options.Create(new SwitchBotOptions
            {
                Enabled = true,
                BaseUrl = globalOptions.Value.BaseUrl,
                Token = token,
                Secret = secret
            });

            return new SwitchBotClient(
                httpClientFactory.CreateClient(HttpClientName),
                perHousehold,
                loggerFactory.CreateLogger<SwitchBotClient>());
        }

        if (globalOptions.Value.AllowGlobalFallback && globalOptions.Value.IsConfigured)
        {
            return new SwitchBotClient(
                httpClientFactory.CreateClient(HttpClientName),
                globalOptions,
                loggerFactory.CreateLogger<SwitchBotClient>());
        }

        return NotConfiguredSwitchBotClient.Instance;
    }

    public ISwitchBotClient CreateAdHocClient(string token, string secret)
    {
        var adHoc = Options.Create(new SwitchBotOptions
        {
            Enabled = true,
            BaseUrl = globalOptions.Value.BaseUrl,
            Token = token,
            Secret = secret
        });

        return new SwitchBotClient(
            httpClientFactory.CreateClient(HttpClientName),
            adHoc,
            loggerFactory.CreateLogger<SwitchBotClient>());
    }

    public async Task<IDeviceProvider> GetDeviceProviderAsync(Guid householdId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(householdId, ct);
        return new SwitchBotDeviceProvider(client, loggerFactory.CreateLogger<SwitchBotDeviceProvider>());
    }

    /// <summary>
    /// Safe "nothing to do here" sentinel: reports IsConfigured = false so callers
    /// skip this household instead of calling a method that would otherwise throw.
    /// </summary>
    private sealed class NotConfiguredSwitchBotClient : ISwitchBotClient
    {
        public static readonly NotConfiguredSwitchBotClient Instance = new();

        public bool IsConfigured => false;

        public Task<string> GetDeviceListRawAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("SwitchBot is not configured for this household.");

        public Task<string> GetDeviceStatusRawAsync(string deviceId, CancellationToken ct = default) =>
            throw new InvalidOperationException("SwitchBot is not configured for this household.");

        public Task<string> SendCommandRawAsync(string deviceId, string command, string parameter, string commandType, CancellationToken ct = default) =>
            throw new InvalidOperationException("SwitchBot is not configured for this household.");
    }
}

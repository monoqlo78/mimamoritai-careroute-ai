using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// Streams Plug Mini telemetry readings into a Fabric Eventhouse (KQL database)
/// table (SwitchBotPlugReadings, distinct from the DeviceEvents table used by
/// <see cref="EventhouseStreamPublisher"/>) using the same raw streaming ingestion
/// REST endpoint, authenticated passwordlessly via Azure.Identity.
///
/// Deliberately mirrors EventhouseStreamPublisher's token-caching/NDJSON/exception
/// handling exactly (rather than sharing code) so a Plug Mini ingestion outage or
/// misconfiguration can never affect DeviceEvent publishing, and vice versa. Must
/// never throw: this is a best-effort secondary write path used by the SwitchBot
/// polling loop only.
/// </summary>
public sealed class EventhousePlugMiniReadingStreamPublisher(
    HttpClient http,
    IOptions<EventhouseOptions> options,
    TokenCredential credential,
    ILogger<EventhousePlugMiniReadingStreamPublisher> logger) : IPlugMiniReadingStreamPublisher
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly EventhouseOptions _options = options.Value;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private AccessToken? _cachedToken;

    public bool IsConfigured => _options.IsConfigured;

    public string DisplayName => "EventhousePlugMiniReading";

    public async Task<EventStreamPublishResult> PublishAsync(
        IReadOnlyList<PlugMiniReadingRecord> readings, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (!IsConfigured)
        {
            return new EventStreamPublishResult(false, 0, 0, "Eventhouse is not configured.");
        }

        if (readings.Count == 0)
        {
            return new EventStreamPublishResult(true, 0, sw.ElapsedMilliseconds);
        }

        try
        {
            var token = await GetTokenAsync(ct);
            var body = BuildNewlineDelimitedJson(readings);

            var url = $"v1/rest/ingest/{_options.DatabaseName}/{_options.PlugMiniTableName}" +
                      $"?streamFormat=json&mappingName={_options.PlugMiniMappingName}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                // Deliberately does not include the response body, which may echo request data.
                logger.LogWarning("Eventhouse Plug Mini ingest failed with {Status}.", (int)response.StatusCode);
                return new EventStreamPublishResult(false, 0, sw.ElapsedMilliseconds,
                    $"Eventhouse returned {(int)response.StatusCode}.");
            }

            return new EventStreamPublishResult(true, readings.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or CredentialUnavailableException
            or Azure.RequestFailedException)
        {
            logger.LogWarning("Eventhouse Plug Mini ingest failed: {Type}.", ex.GetType().Name);
            return new EventStreamPublishResult(false, 0, sw.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    private async Task<AccessToken> GetTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is { } cached && cached.ExpiresOn > DateTimeOffset.UtcNow + RefreshMargin)
        {
            return cached;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken is { } stillCached && stillCached.ExpiresOn > DateTimeOffset.UtcNow + RefreshMargin)
            {
                return stillCached;
            }

            var scope = _options.ClusterUri.TrimEnd('/') + "/.default";
            var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), ct);
            _cachedToken = token;
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static string BuildNewlineDelimitedJson(IReadOnlyList<PlugMiniReadingRecord> readings)
    {
        var sb = new StringBuilder();
        foreach (var r in readings)
        {
            var line = JsonSerializer.Serialize(new
            {
                readingId = r.ReadingId,
                householdId = r.HouseholdId,
                deviceId = r.DeviceId,
                deviceName = r.DeviceName,
                room = r.Room,
                voltageV = r.VoltageV,
                currentMa = r.CurrentMa,
                dailyEnergyWh = r.DailyEnergyWh,
                usageMinutesToday = r.UsageMinutesToday,
                approxWatts = r.ApproxWatts,
                occurredAtUtc = r.OccurredAtUtc.ToString("o")
            }, JsonOptions);
            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }
}

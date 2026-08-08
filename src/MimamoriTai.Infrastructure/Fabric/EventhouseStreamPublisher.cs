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
/// Streams device events into a Fabric Eventhouse (KQL database) using the raw
/// streaming ingestion REST endpoint (newline-delimited JSON), authenticated
/// passwordlessly via Azure.Identity. Deliberately avoids the heavy
/// Microsoft.Azure.Kusto.Ingest SDK: this single HTTP call is all that is needed.
///
/// Must never throw: Azure SQL is the source of truth and this is a best-effort
/// secondary write path used by the SwitchBot polling loop and the manual
/// /api/stream/publish endpoint.
/// </summary>
public sealed class EventhouseStreamPublisher(
    HttpClient http,
    IOptions<EventhouseOptions> options,
    TokenCredential credential,
    ILogger<EventhouseStreamPublisher> logger) : IEventStreamPublisher
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    // Device names/rooms are Japanese; keep them human-readable in the NDJSON body
    // instead of escaping to \uXXXX (both are valid JSON, this is just for clarity).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly EventhouseOptions _options = options.Value;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private AccessToken? _cachedToken;

    public bool IsConfigured => _options.IsConfigured;

    public string DisplayName => "Eventhouse";

    public async Task<EventStreamPublishResult> PublishAsync(
        IReadOnlyList<DeviceEventRecord> events, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (!IsConfigured)
        {
            return new EventStreamPublishResult(false, 0, 0, "Eventhouse is not configured.");
        }

        if (events.Count == 0)
        {
            return new EventStreamPublishResult(true, 0, sw.ElapsedMilliseconds);
        }

        try
        {
            var token = await GetTokenAsync(ct);
            var body = BuildNewlineDelimitedJson(events);

            var url = $"v1/rest/ingest/{_options.DatabaseName}/{_options.TableName}" +
                      $"?streamFormat=json&mappingName={_options.MappingName}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                // Deliberately does not include the response body, which may echo request data.
                logger.LogWarning("Eventhouse ingest failed with {Status}.", (int)response.StatusCode);
                return new EventStreamPublishResult(false, 0, sw.ElapsedMilliseconds,
                    $"Eventhouse returned {(int)response.StatusCode}.");
            }

            return new EventStreamPublishResult(true, events.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or CredentialUnavailableException
            or Azure.RequestFailedException)
        {
            logger.LogWarning("Eventhouse ingest failed: {Type}.", ex.GetType().Name);
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

    private static string BuildNewlineDelimitedJson(IReadOnlyList<DeviceEventRecord> events)
    {
        var sb = new StringBuilder();
        foreach (var e in events)
        {
            var line = JsonSerializer.Serialize(new
            {
                eventId = e.EventId,
                householdId = e.HouseholdId,
                deviceId = e.DeviceId,
                deviceName = e.DeviceName,
                room = e.Room,
                deviceType = e.DeviceType,
                eventType = e.EventType,
                state = e.State,
                powerWatts = e.PowerWatts,
                source = e.Source,
                occurredAtUtc = e.OccurredAtUtc.ToString("o")
            }, JsonOptions);
            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }
}

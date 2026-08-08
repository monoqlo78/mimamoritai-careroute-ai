using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Devices;

/// <summary>
/// Thin HTTP client for the SwitchBot OpenAPI v1.1.
///
/// Only the transport and the documented authentication headers are implemented here.
/// Every call returns the raw JSON body on purpose: the response DTOs are mapped once
/// the physical devices arrive and the official specification has been verified.
/// </summary>
public sealed class SwitchBotClient(HttpClient http, IOptions<SwitchBotOptions> options) : ISwitchBotClient
{
    private readonly SwitchBotOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public Task<string> GetDeviceListRawAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "/v1.1/devices", null, ct);

    public Task<string> GetDeviceStatusRawAsync(string deviceId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"/v1.1/devices/{Uri.EscapeDataString(deviceId)}/status", null, ct);

    public Task<string> SendCommandRawAsync(
        string deviceId, string command, string parameter, string commandType, CancellationToken ct = default)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            command,
            parameter,
            commandType
        });

        return SendAsync(HttpMethod.Post, $"/v1.1/devices/{Uri.EscapeDataString(deviceId)}/commands", body, ct);
    }

    private async Task<string> SendAsync(HttpMethod method, string path, string? jsonBody, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "SwitchBot is not configured. Set SwitchBot:Enabled, SwitchBot:Token and SwitchBot:Secret via user-secrets or environment variables.");
        }

        using var request = new HttpRequestMessage(method, new Uri(new Uri(_options.BaseUrl), path));
        ApplyAuthHeaders(request);

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        // Never surface the token/secret in an exception message.
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"SwitchBot API returned {(int)response.StatusCode}.");
        }

        return content;
    }

    /// <summary>
    /// SwitchBot OpenAPI v1.1 signs requests with
    /// base64(HMAC-SHA256(secret, token + t + nonce)).
    /// </summary>
    private void ApplyAuthHeaders(HttpRequestMessage request)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString("N");
        var payload = $"{_options.Token}{timestamp}{nonce}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.Secret));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));

        request.Headers.TryAddWithoutValidation("Authorization", _options.Token);
        request.Headers.TryAddWithoutValidation("sign", signature);
        request.Headers.TryAddWithoutValidation("t", timestamp);
        request.Headers.TryAddWithoutValidation("nonce", nonce);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}

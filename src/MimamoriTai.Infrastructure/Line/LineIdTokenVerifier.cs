using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MimamoriTai.Infrastructure.Line;

/// <summary>
/// The verified identity behind a LIFF session.
/// </summary>
/// <param name="LineUserId">The LINE user id (`sub`), safe to match against LineRecipient rows.</param>
/// <param name="DisplayName">The user's LINE display name, or null when the profile scope was not granted.</param>
public sealed record LineIdentity(string LineUserId, string? DisplayName);

/// <summary>
/// Verifies the ID token a LIFF page obtains from <c>liff.getIDToken()</c>.
///
/// This exists because the browser half of a LIFF app cannot be trusted to say who it
/// is. <c>liff.getProfile()</c> returns a userId to the page, but anything the page then
/// posts to the server is just a string an attacker could also post -- and here that
/// string selects which family's household data is displayed. LINE's
/// <c>/oauth2/v2.1/verify</c> endpoint re-validates the token's signature, issuer,
/// expiry and audience server-side, so only a token genuinely minted by LINE for *this*
/// channel resolves to a household.
/// </summary>
public interface ILineIdTokenVerifier
{
    /// <summary>True when a LIFF channel id is configured and verification is therefore possible.</summary>
    bool CanVerify { get; }

    /// <summary>
    /// Returns the verified identity, or null when the token is missing, malformed,
    /// expired, issued for another channel, or verification is not configured.
    /// Never throws: a failed verification is an ordinary "not signed in" outcome.
    /// </summary>
    Task<LineIdentity?> VerifyAsync(string? idToken, CancellationToken ct = default);
}

/// <summary>Live implementation calling LINE's token verification endpoint.</summary>
public sealed class LineIdTokenVerifier(
    HttpClient http,
    IOptions<LineOptions> options,
    ILogger<LineIdTokenVerifier> logger) : ILineIdTokenVerifier
{
    private readonly LineOptions _options = options.Value;

    public bool CanVerify => !string.IsNullOrWhiteSpace(_options.LiffChannelId);

    public async Task<LineIdentity?> VerifyAsync(string? idToken, CancellationToken ct = default)
    {
        if (!CanVerify || string.IsNullOrWhiteSpace(idToken))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(new Uri(_options.BaseUrl), "/oauth2/v2.1/verify"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["id_token"] = idToken,
                    ["client_id"] = _options.LiffChannelId
                })
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                // Logged without the token or any claim: an expired token is a routine
                // event (the family left the LIFF view open), not an incident.
                logger.LogInformation("LINE ID token verification returned {Status}.", (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            return ParseIdentity(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("LINE ID token verification failed: {Type}.", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Reads the verified claims. Internal so the parsing can be tested without an
    /// HTTP round trip; a response missing `sub` is treated as a failure rather than
    /// yielding an identity with an empty user id that would match no household.
    /// </summary>
    internal static LineIdentity? ParseIdentity(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("sub", out var sub)
            || sub.ValueKind != JsonValueKind.String
            || sub.GetString() is not { Length: > 0 } lineUserId)
        {
            return null;
        }

        var displayName = root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;

        return new LineIdentity(lineUserId, displayName);
    }
}

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Line;

/// <summary>
/// Shared LINE webhook signature verification.
/// LINE signs the raw request body with HMAC-SHA256 using the channel secret and
/// sends the base64 result in the X-Line-Signature header.
/// </summary>
public static class LineSignature
{
    public const string HeaderName = "X-Line-Signature";

    public static bool Verify(string? channelSecret, string rawBody, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(channelSecret) || string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        byte[] expected;
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(channelSecret)))
        {
            expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody ?? string.Empty));
        }

        byte[] provided;
        try
        {
            provided = Convert.FromBase64String(signatureHeader);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}

/// <summary>
/// DEMO ONLY. Records outbound messages in memory so the LINE Simulator on the
/// dashboard works without any channel credentials.
/// </summary>
public sealed class MockLineMessagingClient(IOptions<LineOptions> options) : ILineMessagingClient
{
    private readonly LineOptions _options = options.Value;
    private readonly List<string> _sent = [];

    public bool IsConfigured => false;

    public IReadOnlyList<string> SentMessages => _sent;

    public Task<LineSendResult> ReplyAsync(string replyToken, string text, CancellationToken ct = default)
    {
        lock (_sent) { _sent.Add(text); }
        return Task.FromResult(new LineSendResult(true));
    }

    public Task<LineSendResult> PushAsync(string to, string text, CancellationToken ct = default)
    {
        lock (_sent) { _sent.Add(text); }
        return Task.FromResult(new LineSendResult(true));
    }

    /// <summary>
    /// Even in mock mode a configured channel secret is honoured, so signature
    /// rejection can be demonstrated and tested.
    /// </summary>
    public bool VerifySignature(string rawBody, string? signatureHeader) =>
        LineSignature.Verify(_options.ChannelSecret, rawBody, signatureHeader);
}

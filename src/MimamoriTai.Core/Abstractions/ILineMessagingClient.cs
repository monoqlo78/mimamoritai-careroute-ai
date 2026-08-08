namespace MimamoriTai.Core.Abstractions;

public sealed record LineSendResult(bool Success, string? Error = null);

public interface ILineMessagingClient
{
    bool IsConfigured { get; }

    Task<LineSendResult> ReplyAsync(string replyToken, string text, CancellationToken ct = default);
    Task<LineSendResult> PushAsync(string to, string text, CancellationToken ct = default);

    /// <summary>
    /// Verifies the X-Line-Signature header (HMAC-SHA256 over the raw body, base64).
    /// Returns false when the channel secret is not configured.
    /// </summary>
    bool VerifySignature(string rawBody, string? signatureHeader);
}

namespace MimamoriTai.Core.Abstractions;

public sealed record LineSendResult(bool Success, string? Error = null);

/// <summary>
/// A watch alert rendered as a card rather than a bare line of text.
///
/// The family reads these on a phone, often at a glance and often at night. A card
/// carrying the 見守り隊 mascot and a colour-coded risk label is recognisable before
/// the text is read, which is the whole reason the character exists.
/// </summary>
/// <param name="Title">Short headline, e.g. "見守りのお知らせ".</param>
/// <param name="Text">The message body. This is also what a text-only fallback sends.</param>
/// <param name="RiskLabel">Human-readable risk level shown as a badge, e.g. "注意".</param>
/// <param name="ImageUrl">Absolute https URL of the mascot image, or null for text only.</param>
/// <param name="LinkUrl">Absolute https URL the "様子をみる" button opens, or null to omit it.</param>
public sealed record LineAlertCard(
    string Title,
    string Text,
    string RiskLabel,
    string? ImageUrl = null,
    string? LinkUrl = null);

public interface ILineMessagingClient
{
    bool IsConfigured { get; }

    Task<LineSendResult> ReplyAsync(string replyToken, string text, CancellationToken ct = default);
    Task<LineSendResult> PushAsync(string to, string text, CancellationToken ct = default);

    /// <summary>
    /// Pushes an alert as a mascot-illustrated card.
    ///
    /// Defaulted to the plain text push so an implementation that cannot render a
    /// card (or a caller running without a public image URL) still delivers the
    /// alert. Losing the illustration must never mean losing the notification.
    /// </summary>
    Task<LineSendResult> PushAlertAsync(string to, LineAlertCard card, CancellationToken ct = default) =>
        PushAsync(to, card.Text, ct);

    /// <summary>
    /// Verifies the X-Line-Signature header (HMAC-SHA256 over the raw body, base64).
    /// Returns false when the channel secret is not configured.
    /// </summary>
    bool VerifySignature(string rawBody, string? signatureHeader);
}

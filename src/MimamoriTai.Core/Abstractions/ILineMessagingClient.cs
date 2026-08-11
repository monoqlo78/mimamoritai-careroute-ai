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

/// <summary>
/// One tappable choice offered under a reply (a LINE "quick reply" chip).
///
/// Typing is the hardest part of this product for its users: an 85 year old with
/// shaky hands can tap 「家族の追加」 far more reliably than they can type it, and a
/// visible list of choices also answers the unasked question of what may be asked
/// at all. Chips are per-reply, so unlike the rich menu they can be offered by any
/// message without a channel-level deployment step.
/// </summary>
/// <param name="Label">What the chip reads, max 20 characters (a LINE limit).</param>
/// <param name="MessageText">Sent as if the user had typed it, or null for a postback chip.</param>
/// <param name="PostbackData">Delivered as a postback, or null for a message chip.</param>
public sealed record LineQuickReply(string Label, string? MessageText = null, string? PostbackData = null)
{
    /// <summary>A chip that sends text. Defaults to sending exactly what it reads.</summary>
    public static LineQuickReply Message(string label, string? text = null) => new(label, text ?? label);

    /// <summary>
    /// A chip that fires an existing rich-menu action, so tapping it runs the same
    /// code path as the button rather than a second, drifting implementation.
    /// </summary>
    public static LineQuickReply Postback(string label, string data) => new(label, null, data);
}

public interface ILineMessagingClient
{
    bool IsConfigured { get; }

    Task<LineSendResult> ReplyAsync(string replyToken, string text, CancellationToken ct = default);

    /// <summary>
    /// Replies with tappable choices attached.
    ///
    /// Defaulted to the plain text reply so an implementation that cannot render
    /// chips still delivers the answer. Losing the choices must never mean losing
    /// the reply, for the same reason <see cref="PushAlertAsync"/> degrades to text.
    /// </summary>
    Task<LineSendResult> ReplyAsync(
        string replyToken,
        string text,
        IReadOnlyList<LineQuickReply> quickReplies,
        CancellationToken ct = default) =>
        ReplyAsync(replyToken, text, ct);
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

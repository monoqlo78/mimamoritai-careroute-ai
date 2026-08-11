namespace MimamoriTai.Infrastructure.Line;

/// <summary>
/// LINE Messaging API settings.
/// ChannelAccessToken / ChannelSecret must come from User Secrets or environment
/// variables. They are never written to appsettings.json.
/// </summary>
public sealed class LineOptions
{
    public const string SectionName = "Line";

    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "https://api.line.me";

    public string ChannelAccessToken { get; set; } = string.Empty;

    public string ChannelSecret { get; set; } = string.Empty;

    /// <summary>
    /// LINE group id or user id that watch/risk anomaly alerts are pushed to.
    /// Safe empty default; never committed as a real id. When empty, WatchAlertService
    /// still evaluates and records what it would have sent, but never calls PushAsync.
    /// </summary>
    public string AlertToId { get; set; } = string.Empty;

    /// <summary>Minimum risk level ("Low" | "Medium" | "High") that triggers an alert.</summary>
    public string AlertRiskThreshold { get; set; } = "Medium";

    /// <summary>Hours to suppress a repeat alert for the same person + risk level.</summary>
    public double AlertCooldownHours { get; set; } = 6;

    /// <summary>Minutes between automatic background evaluations of the watch alert.</summary>
    public double AlertPollIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Absolute https origin of this deployment, used to build the image and button
    /// URLs of the mascot alert card (LINE fetches them from its own servers, so a
    /// relative path or http://localhost cannot work).
    ///
    /// Empty by default: alerts are then sent as plain text, which is the correct
    /// behaviour for a local run and for any environment without a public hostname.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Display name shown on each bubble the bot sends (Messaging API `sender.name`).
    ///
    /// The LINE Official Account's own name and picture can only be changed by a human
    /// in LINE Official Account Manager, but every individual message may override both.
    /// That override is the only part of the account's appearance this codebase can
    /// actually control, so it is used to put ミマモ in front of the family instead of
    /// whatever placeholder the account was registered with.
    ///
    /// LINE rejects a name longer than 20 characters, so an over-long value is dropped
    /// rather than sent: losing the label must never cost the family the message.
    /// </summary>
    public string SenderName { get; set; } = "ミマモ";

    /// <summary>
    /// Root-relative path of the mascot avatar used as the per-message icon
    /// (Messaging API `sender.iconUrl`).
    ///
    /// Combined with <see cref="PublicBaseUrl"/> at send time. LINE fetches the icon
    /// from its own servers, so without a public https origin there is no usable URL
    /// and the sender override is simply omitted -- exactly like the Flex hero image.
    /// </summary>
    public string SenderIconPath { get; set; } = "/images/mimamo-avatar.png";

    /// <summary>
    /// LIFF app id (e.g. "2011034584-abcd1234") issued in the LINE Developers console
    /// for the LIFF app whose endpoint URL points at <c>/liff</c> on this deployment.
    ///
    /// Empty by default and deliberately not inferable: when it is empty the /liff page
    /// renders an explanatory placeholder instead of attempting to boot the LIFF SDK, and
    /// nothing anywhere else in the app links to it. An id cannot be hard-coded because
    /// it is per-channel, and a wrong id makes the in-LINE view fail silently.
    /// </summary>
    public string LiffId { get; set; } = string.Empty;

    /// <summary>
    /// LINE Login channel id that issues the LIFF ID token, used as the `client_id`
    /// audience when verifying that token server-side.
    ///
    /// Empty means "cannot verify", and an unverifiable token is never trusted to
    /// select a household: the LIFF page then stays in its signed-out state rather
    /// than showing one family's data to whoever opened the page.
    /// </summary>
    public string LiffChannelId { get; set; } = string.Empty;

    /// <summary>True when a LIFF app id is configured and the /liff experience should be offered.</summary>
    public bool HasLiff => !string.IsNullOrWhiteSpace(LiffId);

    /// <summary>
    /// Controls the webhook's behavior for a LINE source (userId/groupId) that has
    /// never been linked to any household via a link code or an existing active
    /// LineRecipient row. When true, that source is bound to
    /// HouseholdAccessService.ResolveDefaultAsync (the current demo/local
    /// single-user experience). When false (the default, and the only safe setting
    /// once more than one real household exists), an unlinked source is never
    /// silently attached to any household: it is only ever told, via the follow/
    /// message reply, to send "連携 123456" using a code generated from the
    /// Settings UI. Already-linked sources always resolve their own household
    /// regardless of this flag -- this flag only controls the fallback for
    /// genuinely unknown sources.
    /// </summary>
    public bool AllowDefaultHouseholdFallback { get; set; }

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ChannelAccessToken)
        && !string.IsNullOrWhiteSpace(ChannelSecret);

    public bool HasChannelSecret => !string.IsNullOrWhiteSpace(ChannelSecret);
}

namespace MimamoriTai.Infrastructure.Line;

/// <summary>
/// The per-message identity override LINE renders next to a bubble
/// (Messaging API `sender`).
///
/// This exists because the LINE Official Account's own name and profile picture can
/// only be changed by a human in LINE Official Account Manager -- the Messaging API
/// has no endpoint for it. `sender` is the one lever available from code, and it is
/// what actually puts ミマモ in front of the family on every message the bot sends.
/// </summary>
/// <param name="Name">Display name for this bubble. LINE caps it at 20 characters.</param>
/// <param name="IconUrl">Absolute https URL of the avatar shown beside the bubble.</param>
public sealed record LineSender(string Name, string IconUrl);

/// <summary>
/// Builds the <see cref="LineSender"/> for outgoing messages from the configured
/// public origin, avatar path and display name.
///
/// Kept separate from <see cref="LineMessagingClient"/> so the "when is an override
/// safe to send?" rule can be tested on its own: a malformed sender is not a cosmetic
/// problem, it makes LINE reject the whole request with a 400 and the family simply
/// never receives the alert.
/// </summary>
public static class LineSenderFactory
{
    /// <summary>LINE's documented maximum length for `sender.name`.</summary>
    public const int MaxNameLength = 20;

    /// <summary>
    /// Returns the sender override, or null when one cannot be built safely.
    ///
    /// Null (i.e. "send the message with the account's own name and picture") is
    /// returned when:
    /// <list type="bullet">
    ///   <item>no public origin is configured, or it is not https -- LINE fetches the
    ///   icon from its own servers, so http://localhost can never resolve;</item>
    ///   <item>the icon path or display name is blank;</item>
    ///   <item>the display name exceeds LINE's 20-character limit.</item>
    /// </list>
    /// Every one of those cases degrades to the previous behaviour rather than
    /// risking a rejected send.
    /// </summary>
    public static LineSender? Create(string? publicBaseUrl, string? iconPath, string? name)
    {
        var origin = publicBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(origin)
            || !origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = iconPath?.Trim();
        var displayName = name?.Trim();

        if (string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(displayName)
            || displayName.Length > MaxNameLength)
        {
            return null;
        }

        // An absolute icon URL is honoured as-is so a CDN-hosted avatar can be used
        // without also moving the whole app behind that origin.
        var iconUrl = path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"{origin}/{path.TrimStart('/')}";

        return new LineSender(displayName, iconUrl);
    }

    /// <summary>Convenience overload reading everything from <see cref="LineOptions"/>.</summary>
    public static LineSender? Create(LineOptions options) =>
        Create(options.PublicBaseUrl, options.SenderIconPath, options.SenderName);
}

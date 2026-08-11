namespace MimamoriTai.Infrastructure.Security;

/// <summary>
/// Configuration for the ASP.NET Core Data Protection key ring used to encrypt
/// per-household SwitchBot credentials (<c>DataProtectionCredentialProtector</c>).
///
/// Local development: when <see cref="KeyDirectory"/> is not set, ASP.NET Core falls
/// back to its own default local key ring (typically under the user profile), which
/// is fine for a single-machine dev/demo box and requires no extra configuration.
///
/// Any non-Development environment MUST set <see cref="KeyDirectory"/> to a durable
/// path that survives app restarts/redeploys (e.g. an Azure Files share mounted into
/// an App Service, or a persistent volume) -- see docs/SECURITY.md. The app fails
/// fast at startup in non-Development environments when this is not configured,
/// rather than silently generating an ephemeral key ring that would make every
/// already-saved SwitchBotConnection unreadable after the next restart/deploy.
/// </summary>
public sealed class MimamoriDataProtectionOptions
{
    public const string SectionName = "DataProtection";

    /// <summary>
    /// Absolute path to a durable directory the Data Protection key ring is
    /// persisted to via <c>PersistKeysToFileSystem</c>. Must be configured (and
    /// backed by durable storage) in every non-Development environment.
    /// </summary>
    public string? KeyDirectory { get; set; }

    public bool IsDurablePathConfigured => !string.IsNullOrWhiteSpace(KeyDirectory);
}

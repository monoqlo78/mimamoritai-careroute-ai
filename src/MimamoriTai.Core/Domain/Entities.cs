namespace MimamoriTai.Core.Domain;

public class Household
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether this household is the shared demo dataset (visible to every user) or
    /// a real user's production data (visible only to its <see cref="HouseholdMember"/>s).
    /// </summary>
    public DataSourceMode DataSourceMode { get; set; } = DataSourceMode.Sample;

    public List<Person> People { get; set; } = [];
    public List<Device> Devices { get; set; } = [];
}

public class Person
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public PersonRole Role { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Household? Household { get; set; }
}

public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }

    /// <summary>Identifier used by the upstream provider (SwitchBot deviceId, or a mock id).</summary>
    public string ExternalDeviceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Stable, human friendly key used to resolve natural language references.</summary>
    public string Alias { get; set; } = string.Empty;

    public DeviceType DeviceType { get; set; }
    public string Room { get; set; } = string.Empty;
    public DeviceProviderKind Provider { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool RemoteControlAllowed { get; set; }
    public SafetyClass SafetyClass { get; set; }

    /// <summary>
    /// False when this device was previously synced from a provider (e.g. SwitchBot)
    /// but no longer appears there. Deactivated devices are kept (never deleted) so
    /// their historical DeviceEvent/DeviceCommand rows remain valid, but they are
    /// excluded from the dashboard and from natural language resolution.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Household? Household { get; set; }
}

public class DeviceEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid DeviceId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public double? PowerWatts { get; set; }
    public double? NumericValue { get; set; }
    public string? Unit { get; set; }
    public EventSource Source { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? RawPayloadJson { get; set; }

    /// <summary>
    /// When this row was last streamed to the Fabric Eventhouse, or null if it has
    /// never been published. Drives the incremental publish background service:
    /// only null rows are candidates, and stamping this on success (never on
    /// failure) makes republishing idempotent and safely retryable.
    /// </summary>
    public DateTimeOffset? PublishedToStreamAtUtc { get; set; }

    public Device? Device { get; set; }
}

public class DeviceCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? RequestedByPersonId { get; set; }
    public CommandSource Source { get; set; }
    public string OriginalText { get; set; } = string.Empty;
    public DeviceAction Action { get; set; }
    public CommandStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExecutedAtUtc { get; set; }
    public string? AiResolvedModel { get; set; }

    public Device? Device { get; set; }
}

public class FamilyMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid? PersonId { get; set; }
    public CommandSource Source { get; set; }
    public MessageType MessageType { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Person? Person { get; set; }
}

public class RiskAssessment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid PersonId { get; set; }
    public RiskLevel RiskLevel { get; set; }
    public int Score { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class DailyActivitySummary
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid PersonId { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly? FirstActivityTime { get; set; }
    public TimeOnly? LastActivityTime { get; set; }
    public int DeviceUsageCount { get; set; }
    public int ActiveMinutes { get; set; }
    public int NightActivityCount { get; set; }
    public int RiskScore { get; set; }
    public RiskLevel RiskLevel { get; set; }
}

/// <summary>
/// Records a LINE push notification sent (or attempted) because a watch/risk anomaly
/// was detected. Used to deduplicate repeat alerts for the same person + risk level
/// within a cooldown window.
/// </summary>
public class WatchAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid PersonId { get; set; }
    public RiskLevel RiskLevel { get; set; }
    public int Score { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset SentAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class AiRequestLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? HouseholdId { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string Router { get; set; } = string.Empty;
    public string ResolvedModel { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public bool Success { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A signed-in (or, today, the single dev/demo) user. Identity is keyed by
/// (<see cref="IdentityProvider"/>, <see cref="ExternalSubject"/>) so a later switch to
/// real Entra External ID / LINE OIDC authentication only needs to upsert this row,
/// never change its shape. <see cref="ICurrentUserAccessor"/> is the only coupling
/// point between the auth layer and the rest of the app.
/// </summary>
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>e.g. "dev", "entra-external", "line".</summary>
    public string IdentityProvider { get; set; } = string.Empty;

    /// <summary>The stable subject/oid claim from the IdP.</summary>
    public string ExternalSubject { get; set; } = string.Empty;

    /// <summary>LINE `sub` / userId when known.</summary>
    public string? LineUserId { get; set; }

    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastLoginAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Membership of an <see cref="AppUser"/> in a <see cref="Household"/>, with a role.</summary>
public class HouseholdMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid AppUserId { get; set; }
    public HouseholdMemberRole Role { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Household? Household { get; set; }
    public AppUser? AppUser { get; set; }
}

/// <summary>
/// A LINE user (or group) that has added the bot as a friend / sent it a message, captured
/// via the LINE webhook's `follow`/`message` events. Alert pushes target every active
/// recipient of a household instead of a single hard-coded id, so onboarding a new family
/// member is just "add the bot as a friend" with no manual copy/paste of LINE user ids.
/// </summary>
public class LineRecipient
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }

    /// <summary>LINE `userId` (1:1 chat) or `groupId` (group chat) — both are valid `to` values for push.</summary>
    public string LineUserId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>False once the corresponding `unfollow` webhook event is received.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public Household? Household { get; set; }
}

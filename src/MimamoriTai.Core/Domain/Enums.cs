namespace MimamoriTai.Core.Domain;

public enum PersonRole
{
    Resident = 0,
    Family = 1,
    Admin = 2
}

public enum DeviceType
{
    Unknown = 0,
    Light = 1,
    Fan = 2,
    Plug = 3,
    MotionSensor = 4,
    ContactSensor = 5,
    Heater = 6,
    Kettle = 7,
    Microwave = 8,
    CookingDevice = 9,
    DemoDevice = 10
}

/// <summary>
/// Safety classification used by the natural language control guard rails.
/// Only <see cref="Safe"/> devices may be switched on through an AI resolved intent.
/// </summary>
public enum SafetyClass
{
    Safe = 0,
    Restricted = 1
}

public enum DeviceProviderKind
{
    Mock = 0,
    SwitchBot = 1
}

public enum CommandSource
{
    Web = 0,
    Line = 1,
    System = 2
}

public enum DeviceAction
{
    TurnOn = 0,
    TurnOff = 1,
    Toggle = 2,
    GetStatus = 3
}

public enum CommandStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    Rejected = 3
}

public enum RiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2
}

public enum MessageType
{
    Text = 0,
    AiReply = 1,
    Notice = 2
}

public enum AssistantIntent
{
    Conversation = 0,
    ControlDevice = 1,
    DeviceStatus = 2,
    QueryData = 3
}

public enum EventSource
{
    Mock = 0,
    SwitchBotWebhook = 1,
    SwitchBotPoll = 2,
    AppCommand = 3,
    Simulator = 4,
    Seed = 5
}

/// <summary>
/// Whether a household's data is the shared demo dataset or a real user's
/// production data. Drives both device-provider selection and per-user access
/// control (Sample households are visible to everyone; Production households are
/// only visible to their <see cref="HouseholdMember"/>s).
/// </summary>
public enum DataSourceMode
{
    Sample = 0,
    Production = 1
}

/// <summary>Role of an <see cref="AppUser"/> within a <see cref="Household"/>.</summary>
public enum HouseholdMemberRole
{
    Owner = 0,
    Member = 1,
    Viewer = 2
}

/// <summary>
/// Connection status of a household's per-household SwitchBot credentials
/// (<see cref="SwitchBotConnection"/>). Drives the Settings UI badge and whether the
/// household-scoped polling loop attempts to poll this household at all.
/// </summary>
public enum SwitchBotConnectionStatus
{
    /// <summary>No Token/Secret has been saved for this household yet.</summary>
    NotConfigured = 0,

    /// <summary>Credentials were saved and the most recent validation/sync succeeded.</summary>
    Connected = 1,

    /// <summary>Credentials were saved but the most recent validation/sync failed (e.g. revoked token).</summary>
    Error = 2
}

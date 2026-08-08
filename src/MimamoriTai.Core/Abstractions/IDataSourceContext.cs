using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// Scoped, mutable ambient state describing which household/data-source the current
/// request or dashboard reload is operating against. <see cref="IDeviceProvider"/> is
/// registered as a decorator that reads <see cref="Mode"/> on every call (see
/// <c>DataSourceAwareDeviceProvider</c> in Infrastructure), so setting this once at
/// the top of a unit of work (a dashboard reload, an endpoint handler, a background
/// poll iteration) is all that is needed for every downstream service
/// (<c>DeviceControlService</c>, <c>DeviceSyncService</c>, <c>AssistantOrchestrator</c>)
/// to transparently use the right provider without any code changes at their call sites.
/// </summary>
public interface IDataSourceContext
{
    DataSourceMode Mode { get; set; }
    Guid? HouseholdId { get; set; }
}

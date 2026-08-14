using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// One appliance that heats or burns was switched on remotely, and the whole household
/// is about to be told.
/// </summary>
/// <param name="HouseholdId">Whose home this happened in.</param>
/// <param name="DeviceName">The name the family uses, not the vendor label.</param>
/// <param name="RoomName">Where it is, or empty when nobody has said.</param>
/// <param name="Source">Whether this came from the web, from LINE, or from a job.</param>
/// <param name="OccurredAtUtc">When the command was carried out.</param>
public sealed record GuardedActionNotice(
    Guid HouseholdId,
    string DeviceName,
    string RoomName,
    CommandSource Source,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Announces a remote switch-on of a <see cref="SafetyClass.Guarded"/> appliance to
/// every family member, not just to whoever pressed the button.
///
/// <para>
/// This is the other half of allowing it at all. One relative deciding to warm the room
/// from three prefectures away is reasonable; that decision going unseen by everyone
/// else is not, because the person best placed to say "she has the futon drying right
/// next to it" is whoever was there last. Broadcasting is therefore part of the
/// permission, not a nicety layered on top.
/// </para>
///
/// <para>
/// Implementations must never throw and must never block the command: the appliance has
/// already been switched on by the time this is called, so a failed push has to be
/// swallowed rather than turned into an error the family reads as "it did not work".
/// </para>
/// </summary>
public interface IGuardedActionNotifier
{
    Task NotifyAsync(GuardedActionNotice notice, CancellationToken ct = default);
}

using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>A device action the assistant proposed and is waiting to be confirmed.</summary>
/// <param name="RequiresHazardAcknowledgement">
/// True when the confirmation being awaited is the surroundings check for a
/// <see cref="SafetyClass.Guarded"/> appliance, rather than an ordinary "shall I?".
/// A yes then carries consent that the area was checked, which is what lets the
/// control service energise a heater at all.
/// </param>
public sealed record PendingDeviceAction(
    Guid HouseholdId,
    string DeviceAlias,
    string DeviceName,
    DeviceAction Action,
    string OriginalText,
    DateTimeOffset ProposedAtUtc,
    bool RequiresHazardAcknowledgement = false);

/// <summary>
/// Holds the one device action per household that the assistant has proposed but not
/// yet executed, so a follow-up "はい" can carry it out.
///
/// Deliberately short-lived and single-slot: a stale proposal must never be executed by
/// an unrelated later "はい", and the assistant must not be able to queue up a batch of
/// pending changes to the home.
/// </summary>
public interface IPendingActionStore
{
    void Set(PendingDeviceAction action);

    /// <summary>Returns the pending action if it exists and has not expired, and clears it either way.</summary>
    PendingDeviceAction? Take(Guid householdId, DateTimeOffset nowUtc);

    void Clear(Guid householdId);
}

public sealed class InMemoryPendingActionStore : IPendingActionStore
{
    /// <summary>
    /// A confirmation the family did not answer promptly is no longer about the situation
    /// they were looking at, so it must expire rather than linger.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);

    private readonly Dictionary<Guid, PendingDeviceAction> _pending = [];
    private readonly Lock _gate = new();

    public void Set(PendingDeviceAction action)
    {
        lock (_gate)
        {
            _pending[action.HouseholdId] = action;
        }
    }

    public PendingDeviceAction? Take(Guid householdId, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (!_pending.Remove(householdId, out var action))
            {
                return null;
            }

            return nowUtc - action.ProposedAtUtc <= Lifetime ? action : null;
        }
    }

    public void Clear(Guid householdId)
    {
        lock (_gate)
        {
            _pending.Remove(householdId);
        }
    }
}

/// <summary>
/// Recognises the short replies a family member actually types when answering a
/// confirmation prompt. Anything that is not clearly yes or no is treated as a new
/// instruction, never as consent.
/// </summary>
public static class ConfirmationReply
{
    private static readonly string[] Yes =
    [
        "はい", "うん", "ok", "okay", "オーケー", "おk", "yes", "y",
        "お願い", "おねがい", "頼む", "たのむ", "そう", "実行", "やって", "して", "いいよ", "良いよ", "どうぞ"
    ];

    private static readonly string[] No =
    [
        "いいえ", "いえ", "no", "n", "やめて", "やめる", "キャンセル", "cancel",
        "中止", "だめ", "ダメ", "結構", "けっこう", "取り消し", "ちがう", "違う"
    ];

    public static bool? Interpret(string message)
    {
        var text = Normalize(message);

        if (text.Length == 0)
        {
            return null;
        }

        // Checked before yes so "はい、やめて" is read as a refusal.
        if (No.Any(n => text.Contains(n, StringComparison.Ordinal)))
        {
            return false;
        }

        return Yes.Any(y => text.Contains(y, StringComparison.Ordinal)) ? true : null;
    }

    private static string Normalize(string message) =>
        message.Trim()
            .ToLowerInvariant()
            .Replace(" ", string.Empty)
            .Replace("　", string.Empty)
            .Replace("。", string.Empty)
            .Replace("、", string.Empty)
            .Replace("！", string.Empty)
            .Replace("!", string.Empty);
}

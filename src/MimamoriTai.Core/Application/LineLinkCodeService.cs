using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>Returned once, at generation time, to the household owner. Never persisted.</summary>
public sealed record LineLinkCodeGenerated(string Code, DateTimeOffset ExpiresAtUtc);

public enum LineLinkCodeRedeemStatus
{
    /// <summary>The code matched an active, unused, unexpired <see cref="LineLinkCode"/> row.</summary>
    Success,

    /// <summary>
    /// No active code matched. Deliberately a single catch-all (never distinguishes
    /// "wrong code" from "expired" from "already used" from "attempt limit reached")
    /// so a failed redemption never tells an attacker which of those is true.
    /// </summary>
    Failed,
}

public sealed record LineLinkCodeRedeemResult(LineLinkCodeRedeemStatus Status, Guid? HouseholdId);

/// <summary>
/// Generates and redeems the short-lived "連携 123456" pairing codes that let a
/// household owner link a LINE Messaging API source (userId/groupId) to their
/// household without the webhook ever having to guess (see docs/LINE_SETUP.md).
///
/// Hashing: <see cref="LineLinkCode.CodeHash"/> is an HMAC-SHA256 of the plaintext
/// code keyed with a fixed, non-secret, permanently-stable string (mirrors the
/// fixed Data Protection purpose string convention used elsewhere in this
/// codebase). This is intentionally NOT a secret pepper: the actual security
/// controls protecting a link code are its 10-minute expiry, single-use
/// (<see cref="LineLinkCode.UsedAtUtc"/>) and system-wide attempt-limiting
/// (<see cref="LineLinkCode.AttemptCount"/>) below, not the secrecy of the hash
/// key -- a fixed key only prevents a raw DB dump/read-replica leak from
/// trivially exposing pending plaintext codes; it is not meant to resist a
/// targeted offline brute force, which the attempt limit already stops online.
/// </summary>
public sealed class LineLinkCodeService(IAppDbContext db, TimeProvider clock)
{
    /// <summary>How long a generated code remains redeemable.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Failed redemption attempts allowed against the pool of currently-active codes
    /// before every currently-active code is force-expired. Deliberately small: a
    /// legitimate user mistyping a 6-digit code a couple of times is expected;
    /// dozens of misses in a 10-minute window is not.
    /// </summary>
    public const int MaxAttempts = 5;

    private const string HashKey = "MimamoriTai.LineLinkCode.v1";

    /// <summary>Only a Household Owner may generate a link code for this household.</summary>
    public async Task<bool> IsOwnerAsync(Guid householdId, Guid appUserId, CancellationToken ct = default) =>
        await db.HouseholdMembers.AnyAsync(
            m => m.HouseholdId == householdId && m.AppUserId == appUserId && m.Role == HouseholdMemberRole.Owner, ct);

    /// <summary>
    /// Invalidates any prior unused code for this household (only one may be active
    /// at a time -- see the <see cref="LineLinkCode"/> doc comment) and generates a
    /// new one. The plaintext code is returned exactly once and never stored.
    /// </summary>
    public async Task<LineLinkCodeGenerated> GenerateCodeAsync(Guid householdId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        var priorUnused = await db.LineLinkCodes
            .Where(c => c.HouseholdId == householdId && c.UsedAtUtc == null)
            .ToListAsync(ct);
        if (priorUnused.Count > 0)
        {
            db.LineLinkCodes.RemoveRange(priorUnused);
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var expiresAtUtc = now + CodeLifetime;

        db.LineLinkCodes.Add(new LineLinkCode
        {
            HouseholdId = householdId,
            CodeHash = ComputeHash(code),
            ExpiresAtUtc = expiresAtUtc,
            AttemptCount = 0,
            CreatedAtUtc = now
        });

        await db.SaveChangesAsync(ct);
        return new LineLinkCodeGenerated(code, expiresAtUtc);
    }

    /// <summary>
    /// Attempts to redeem <paramref name="rawCode"/> and link <paramref name="lineUserId"/>
    /// to the matched code's household. On success, upserts an active
    /// <see cref="LineRecipient"/> row for that household + user id, deactivating any
    /// active <see cref="LineRecipient"/> row this same <paramref name="lineUserId"/>
    /// held in a *different* household first (a LINE user id should resolve to
    /// exactly one currently-active household at a time; re-linking is an explicit,
    /// deliberate action by the owner of the new household holding the code, so it
    /// is allowed to supersede a stale link -- see docs/LINE_SETUP.md).
    /// </summary>
    public async Task<LineLinkCodeRedeemResult> RedeemCodeAsync(
        string rawCode, string lineUserId, string? displayName, CancellationToken ct = default)
    {
        var normalized = NormalizeCode(rawCode);
        var now = clock.GetUtcNow();

        var active = await db.LineLinkCodes
            .Where(c => c.UsedAtUtc == null && c.ExpiresAtUtc > now)
            .ToListAsync(ct);

        LineLinkCode? match = null;
        if (normalized is not null)
        {
            var normalizedHash = ComputeHash(normalized);
            foreach (var candidate in active)
            {
                if (FixedTimeHashEquals(candidate.CodeHash, normalizedHash))
                {
                    match = candidate;
                    break;
                }
            }
        }

        if (match is null)
        {
            // Every currently-active code absorbs a strike, not just the (nonexistent)
            // "targeted" one -- see the AttemptCount doc comment on LineLinkCode.
            foreach (var candidate in active)
            {
                candidate.AttemptCount++;
                if (candidate.AttemptCount >= MaxAttempts)
                {
                    candidate.ExpiresAtUtc = now;
                }
            }

            if (active.Count > 0)
            {
                await db.SaveChangesAsync(ct);
            }

            return new LineLinkCodeRedeemResult(LineLinkCodeRedeemStatus.Failed, null);
        }

        match.UsedAtUtc = now;

        // Supersede any active recipient row this LINE user id held in a different household.
        var otherHouseholdRows = await db.LineRecipients
            .Where(r => r.LineUserId == lineUserId && r.HouseholdId != match.HouseholdId && r.IsActive)
            .ToListAsync(ct);
        foreach (var stale in otherHouseholdRows)
        {
            stale.IsActive = false;
        }

        var recipient = await db.LineRecipients
            .FirstOrDefaultAsync(r => r.HouseholdId == match.HouseholdId && r.LineUserId == lineUserId, ct);
        if (recipient is null)
        {
            db.LineRecipients.Add(new LineRecipient
            {
                HouseholdId = match.HouseholdId,
                LineUserId = lineUserId,
                DisplayName = displayName,
                IsActive = true,
                CreatedAt = now,
                LastSeenAt = now
            });
        }
        else
        {
            recipient.IsActive = true;
            recipient.LastSeenAt = now;
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                recipient.DisplayName = displayName;
            }
        }

        await db.SaveChangesAsync(ct);
        return new LineLinkCodeRedeemResult(LineLinkCodeRedeemStatus.Success, match.HouseholdId);
    }

    /// <summary>
    /// Strips whitespace and converts full-width digits (０-９) to ASCII digits.
    /// Returns null if the result is not exactly 6 ASCII digits.
    /// </summary>
    internal static string? NormalizeCode(string? rawCode)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            return null;
        }

        var builder = new StringBuilder(rawCode.Length);
        foreach (var c in rawCode)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (c is >= '０' and <= '９')
            {
                builder.Append((char)(c - '０' + '0'));
            }
            else
            {
                builder.Append(c);
            }
        }

        var normalized = builder.ToString();
        return normalized.Length == 6 && normalized.All(char.IsAsciiDigit) ? normalized : null;
    }

    private static string ComputeHash(string code)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(HashKey));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(code)));
    }

    private static bool FixedTimeHashEquals(string storedHash, string candidateHash)
    {
        var a = Encoding.UTF8.GetBytes(storedHash);
        var b = Encoding.UTF8.GetBytes(candidateHash);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

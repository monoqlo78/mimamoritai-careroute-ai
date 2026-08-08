using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;

namespace MimamoriTai.Infrastructure.Line;

/// <summary>
/// DB-backed <see cref="ILineRecipientResolver"/>. An explicit <see cref="WatchAlertSettings.ToId"/>
/// (from LineOptions.AlertToId) always wins, preserving the original manual-configuration path;
/// otherwise every active, self-registered <see cref="Core.Domain.LineRecipient"/> for the
/// household is used, so a new family member only needs to add the bot as a friend.
/// </summary>
public sealed class LineRecipientResolver(IAppDbContext db, WatchAlertSettings settings) : ILineRecipientResolver
{
    public async Task<IReadOnlyList<string>> ResolveAsync(Guid householdId, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(settings.ToId))
        {
            return [settings.ToId];
        }

        return await db.LineRecipients
            .Where(r => r.HouseholdId == householdId && r.IsActive)
            .Select(r => r.LineUserId)
            .ToListAsync(ct);
    }
}

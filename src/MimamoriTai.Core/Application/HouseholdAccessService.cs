using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

public sealed record HouseholdSummary(Guid Id, string Name, DataSourceMode DataSourceMode, HouseholdMemberRole Role);

/// <summary>
/// Central authorization + user-provisioning point for the multi-user model.
/// Rule: <see cref="DataSourceMode.Sample"/> households are the shared demo dataset
/// and are visible to every user; <see cref="DataSourceMode.Production"/> households
/// are visible only to their <see cref="HouseholdMember"/>s. Every dashboard load and
/// endpoint that takes a household id must call <see cref="CanAccessAsync"/> before
/// touching that household's data.
/// </summary>
public sealed class HouseholdAccessService(
    IAppDbContext db,
    ICurrentUserAccessor currentUserAccessor,
    TimeProvider clock)
{
    /// <summary>Upserts the AppUser row for the given identity, refreshing profile fields and LastLoginAtUtc.</summary>
    public async Task<AppUser> EnsureUserAsync(CurrentUser user, CancellationToken ct = default)
    {
        var appUser = await db.AppUsers.FirstOrDefaultAsync(
            u => u.IdentityProvider == user.IdentityProvider && u.ExternalSubject == user.ExternalSubject, ct);

        var now = clock.GetUtcNow();

        if (appUser is null)
        {
            appUser = new AppUser
            {
                Id = user.AppUserId,
                IdentityProvider = user.IdentityProvider,
                ExternalSubject = user.ExternalSubject,
                DisplayName = user.DisplayName,
                CreatedAtUtc = now,
                LastLoginAtUtc = now
            };
            db.AppUsers.Add(appUser);
        }
        else
        {
            appUser.DisplayName = user.DisplayName;
            appUser.LastLoginAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
        return appUser;
    }

    /// <summary>
    /// Every Sample household (shared demo data) plus the Production households the
    /// current user is a member of. Returns an empty list for an anonymous caller.
    /// </summary>
    public async Task<IReadOnlyList<HouseholdSummary>> ListAccessibleAsync(CancellationToken ct = default)
    {
        var user = currentUserAccessor.Current;

        var sample = await db.Households
            .Where(h => h.DataSourceMode == DataSourceMode.Sample)
            .OrderBy(h => h.CreatedAtUtc)
            .Select(h => new HouseholdSummary(h.Id, h.Name, h.DataSourceMode, HouseholdMemberRole.Viewer))
            .ToListAsync(ct);

        if (user is null)
        {
            return sample;
        }

        var owned = await (
            from m in db.HouseholdMembers
            join h in db.Households on m.HouseholdId equals h.Id
            where m.AppUserId == user.AppUserId && h.DataSourceMode == DataSourceMode.Production
            orderby h.CreatedAtUtc
            select new HouseholdSummary(h.Id, h.Name, h.DataSourceMode, m.Role))
            .ToListAsync(ct);

        return [.. sample, .. owned];
    }

    /// <summary>Sample households are accessible to anyone; Production households require membership.</summary>
    public async Task<bool> CanAccessAsync(Guid householdId, CancellationToken ct = default)
    {
        var household = await db.Households
            .Where(h => h.Id == householdId)
            .Select(h => new { h.DataSourceMode })
            .FirstOrDefaultAsync(ct);

        if (household is null)
        {
            return false;
        }

        if (household.DataSourceMode == DataSourceMode.Sample)
        {
            return true;
        }

        var user = currentUserAccessor.Current;
        if (user is null)
        {
            return false;
        }

        return await db.HouseholdMembers.AnyAsync(
            m => m.HouseholdId == householdId && m.AppUserId == user.AppUserId, ct);
    }

    /// <summary>
    /// Idempotent: if the current user already owns a Production household, returns it
    /// unchanged. Otherwise creates one plus an Owner membership and a resident Person.
    /// </summary>
    public async Task<Guid> EnsureProductionHouseholdAsync(string name, CancellationToken ct = default)
    {
        var user = currentUserAccessor.Current
            ?? throw new InvalidOperationException("A signed-in user is required to create a production household.");

        // Self-healing: guarantee the AppUser row exists even if the caller never went
        // through the seeder or an explicit EnsureUserAsync call (e.g. a fresh test DB).
        await EnsureUserAsync(user, ct);

        var existing = await (
            from m in db.HouseholdMembers
            join h in db.Households on m.HouseholdId equals h.Id
            where m.AppUserId == user.AppUserId
                && h.DataSourceMode == DataSourceMode.Production
                && m.Role == HouseholdMemberRole.Owner
            select h.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != Guid.Empty)
        {
            return existing;
        }

        var now = clock.GetUtcNow();

        var household = new Household
        {
            Name = name,
            DataSourceMode = DataSourceMode.Production,
            CreatedAtUtc = now
        };
        db.Households.Add(household);

        db.HouseholdMembers.Add(new HouseholdMember
        {
            HouseholdId = household.Id,
            AppUserId = user.AppUserId,
            Role = HouseholdMemberRole.Owner,
            CreatedAtUtc = now
        });

        db.People.Add(new Person
        {
            HouseholdId = household.Id,
            DisplayName = user.DisplayName,
            Role = PersonRole.Resident,
            CreatedAtUtc = now
        });

        await db.SaveChangesAsync(ct);
        return household.Id;
    }

    /// <summary>The user's own Production household if any, otherwise the oldest Sample household.</summary>
    public async Task<Guid?> ResolveDefaultAsync(CancellationToken ct = default)
    {
        var user = currentUserAccessor.Current;

        if (user is not null)
        {
            var owned = await (
                from m in db.HouseholdMembers
                join h in db.Households on m.HouseholdId equals h.Id
                where m.AppUserId == user.AppUserId && h.DataSourceMode == DataSourceMode.Production
                orderby h.CreatedAtUtc
                select (Guid?)h.Id)
                .FirstOrDefaultAsync(ct);

            if (owned is not null)
            {
                return owned;
            }
        }

        return await db.Households
            .Where(h => h.DataSourceMode == DataSourceMode.Sample)
            .OrderBy(h => h.CreatedAtUtc)
            .Select(h => (Guid?)h.Id)
            .FirstOrDefaultAsync(ct);
    }
}

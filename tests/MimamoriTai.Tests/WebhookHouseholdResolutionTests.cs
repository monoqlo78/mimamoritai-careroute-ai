using MimamoriTai.Core.Domain;
using MimamoriTai.Web.Endpoints;

namespace MimamoriTai.Tests;

/// <summary>
/// Regression coverage for the webhook's critical household-resolution fix:
/// an already-linked LINE source must resolve directly to its own household via
/// <see cref="WebhookEndpoints.ResolveLinkedHouseholdAsync"/>, and must never fall
/// through to the default-household fallback. These tests exercise the resolver
/// helper directly (it is internal, exposed to this assembly via
/// InternalsVisibleTo) rather than standing up a full HTTP test host, since the
/// resolution decision itself -- not the surrounding HTTP/JSON plumbing already
/// covered by LineWebhookEventsTests -- is the security-relevant logic.
/// </summary>
public class WebhookHouseholdResolutionTests
{
    private const string LinkedUserId = "Utestuser0000000000000000000000000";
    private const string UnlinkedUserId = "Utestuser0000000000000000000000009";

    [Fact]
    public async Task ResolveLinkedHouseholdAsync_Returns_The_Household_Of_An_Active_Recipient()
    {
        using var db = await new TestDb().SeedAsync();
        db.Context.LineRecipients.Add(new LineRecipient
        {
            HouseholdId = db.HouseholdId,
            LineUserId = LinkedUserId,
            IsActive = true
        });
        await db.Context.SaveChangesAsync();

        var resolved = await WebhookEndpoints.ResolveLinkedHouseholdAsync(db.Context, LinkedUserId, CancellationToken.None);

        Assert.Equal(db.HouseholdId, resolved);
    }

    [Fact]
    public async Task ResolveLinkedHouseholdAsync_Returns_Null_For_An_Unknown_Source()
    {
        using var db = await new TestDb().SeedAsync();

        var resolved = await WebhookEndpoints.ResolveLinkedHouseholdAsync(db.Context, UnlinkedUserId, CancellationToken.None);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task ResolveLinkedHouseholdAsync_Returns_Null_For_An_Inactive_Recipient()
    {
        using var db = await new TestDb().SeedAsync();
        db.Context.LineRecipients.Add(new LineRecipient
        {
            HouseholdId = db.HouseholdId,
            LineUserId = LinkedUserId,
            IsActive = false
        });
        await db.Context.SaveChangesAsync();

        var resolved = await WebhookEndpoints.ResolveLinkedHouseholdAsync(db.Context, LinkedUserId, CancellationToken.None);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task ResolveLinkedHouseholdAsync_Returns_Null_For_A_Blank_SourceId()
    {
        using var db = await new TestDb().SeedAsync();

        Assert.Null(await WebhookEndpoints.ResolveLinkedHouseholdAsync(db.Context, null, CancellationToken.None));
        Assert.Null(await WebhookEndpoints.ResolveLinkedHouseholdAsync(db.Context, string.Empty, CancellationToken.None));
        Assert.Null(await WebhookEndpoints.ResolveLinkedHouseholdAsync(db.Context, "   ", CancellationToken.None));
    }

    /// <summary>
    /// The bug this whole feature fixes: a source that is actively linked to
    /// household B must resolve to B, never to A, even though A is a different
    /// household that also exists in the database (e.g. the shared demo/default
    /// household). Simulates two distinct households and confirms cross-talk never
    /// happens.
    /// </summary>
    [Fact]
    public async Task ResolveLinkedHouseholdAsync_Never_Crosses_Households_For_Different_Sources()
    {
        using var db = await new TestDb().SeedAsync();
        var householdB = new Household { Name = "別の家族" };
        db.Context.Households.Add(householdB);

        db.Context.LineRecipients.AddRange(
            new LineRecipient { HouseholdId = db.HouseholdId, LineUserId = "Utestuser_A", IsActive = true },
            new LineRecipient { HouseholdId = householdB.Id, LineUserId = "Utestuser_B", IsActive = true });
        await db.Context.SaveChangesAsync();

        var resolvedA = await WebhookEndpoints.ResolveLinkedHouseholdAsync(db.Context, "Utestuser_A", CancellationToken.None);
        var resolvedB = await WebhookEndpoints.ResolveLinkedHouseholdAsync(db.Context, "Utestuser_B", CancellationToken.None);

        Assert.Equal(db.HouseholdId, resolvedA);
        Assert.Equal(householdB.Id, resolvedB);
        Assert.NotEqual(resolvedA, resolvedB);
    }

    [Fact]
    public async Task UpsertRecipientAsync_Creates_An_Active_Row_For_A_New_Source()
    {
        using var db = await new TestDb().SeedAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        await WebhookEndpoints.UpsertRecipientAsync(db.Context, db.HouseholdId, LinkedUserId, isActive: true, clock, CancellationToken.None);

        var recipient = Assert.Single(db.Context.LineRecipients);
        Assert.Equal(db.HouseholdId, recipient.HouseholdId);
        Assert.Equal(LinkedUserId, recipient.LineUserId);
        Assert.True(recipient.IsActive);
    }
}

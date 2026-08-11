using Microsoft.AspNetCore.Http;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Web.Endpoints;

namespace MimamoriTai.Tests;

/// <summary>
/// Unit-tests the SwitchBot connection endpoints' shared ownership guard
/// (<see cref="SwitchBotConnectionEndpoints.RequireOwnerAsync"/>) directly, mirroring
/// WebhookHouseholdResolutionTests' scope trade-off: this project has no
/// WebApplicationFactory/TestServer harness yet, so full HTTP-level request/response
/// behavior (antiforgery rejection, JSON body shape, route matching) is not covered
/// here -- only the authorization decision the guard makes is unit tested.
/// </summary>
public class SwitchBotConnectionEndpointsTests
{
    [Fact]
    public async Task RequireOwnerAsync_Rejects_An_Anonymous_Caller_With_401()
    {
        using var db = await new TestDb().SeedAsync();
        var service = new SwitchBotConnectionService(db.Context, new FakeCredentialProtector(), new FakeHouseholdSwitchBotClientFactory(), new FakeTimeProvider(DateTimeOffset.UtcNow));
        var anonymous = new FakeCurrentUserAccessor(current: null);

        var result = await SwitchBotConnectionEndpoints.RequireOwnerAsync(db.HouseholdId, anonymous, service, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, GetStatusCode(result!));
    }

    [Fact]
    public async Task RequireOwnerAsync_Rejects_A_Signed_In_NonOwner_With_403()
    {
        using var db = await new TestDb().SeedAsync();
        var service = new SwitchBotConnectionService(db.Context, new FakeCredentialProtector(), new FakeHouseholdSwitchBotClientFactory(), new FakeTimeProvider(DateTimeOffset.UtcNow));

        var member = new AppUser { DisplayName = "メンバー", IdentityProvider = "dev", ExternalSubject = "member-sub" };
        db.Context.AppUsers.Add(member);
        db.Context.HouseholdMembers.Add(new HouseholdMember { HouseholdId = db.HouseholdId, AppUserId = member.Id, Role = HouseholdMemberRole.Member });
        await db.Context.SaveChangesAsync();

        var currentUser = FakeCurrentUserAccessor.User(member.Id, member.DisplayName);
        var accessor = new FakeCurrentUserAccessor(currentUser);

        var result = await SwitchBotConnectionEndpoints.RequireOwnerAsync(db.HouseholdId, accessor, service, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status403Forbidden, GetStatusCode(result!));
    }

    [Fact]
    public async Task RequireOwnerAsync_Allows_The_Household_Owner_Through()
    {
        using var db = await new TestDb().SeedAsync();
        var service = new SwitchBotConnectionService(db.Context, new FakeCredentialProtector(), new FakeHouseholdSwitchBotClientFactory(), new FakeTimeProvider(DateTimeOffset.UtcNow));

        var owner = new AppUser { DisplayName = "オーナー", IdentityProvider = "dev", ExternalSubject = "owner-sub" };
        db.Context.AppUsers.Add(owner);
        db.Context.HouseholdMembers.Add(new HouseholdMember { HouseholdId = db.HouseholdId, AppUserId = owner.Id, Role = HouseholdMemberRole.Owner });
        await db.Context.SaveChangesAsync();

        var currentUser = FakeCurrentUserAccessor.User(owner.Id, owner.DisplayName);
        var accessor = new FakeCurrentUserAccessor(currentUser);

        var result = await SwitchBotConnectionEndpoints.RequireOwnerAsync(db.HouseholdId, accessor, service, CancellationToken.None);

        Assert.Null(result); // null == "allowed, proceed"
    }

    [Fact]
    public async Task RequireOwnerAsync_Rejects_An_Owner_Of_A_Different_Household()
    {
        using var db = await new TestDb().SeedAsync();
        var service = new SwitchBotConnectionService(db.Context, new FakeCredentialProtector(), new FakeHouseholdSwitchBotClientFactory(), new FakeTimeProvider(DateTimeOffset.UtcNow));

        // A second household within the same context (FK-safe), so we can prove an
        // owner of household B is rejected when checked against household A.
        var householdB = new Household { Name = "別の家族" };
        db.Context.Households.Add(householdB);

        var ownerOfB = new AppUser { DisplayName = "別世帯オーナー", IdentityProvider = "dev", ExternalSubject = "owner-b-sub" };
        db.Context.AppUsers.Add(ownerOfB);
        await db.Context.SaveChangesAsync();

        db.Context.HouseholdMembers.Add(new HouseholdMember { HouseholdId = householdB.Id, AppUserId = ownerOfB.Id, Role = HouseholdMemberRole.Owner });
        await db.Context.SaveChangesAsync();

        var currentUser = FakeCurrentUserAccessor.User(ownerOfB.Id, ownerOfB.DisplayName);
        var accessor = new FakeCurrentUserAccessor(currentUser);

        // Checking against household A (where this user has no membership row at all).
        var result = await SwitchBotConnectionEndpoints.RequireOwnerAsync(db.HouseholdId, accessor, service, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status403Forbidden, GetStatusCode(result!));
    }

    private static int? GetStatusCode(IResult result)
    {
        // IResult implementations here (Results.Json) implement IStatusCodeHttpResult.
        return result is IStatusCodeHttpResult statusResult
            ? statusResult.StatusCode
            : null;
    }
}

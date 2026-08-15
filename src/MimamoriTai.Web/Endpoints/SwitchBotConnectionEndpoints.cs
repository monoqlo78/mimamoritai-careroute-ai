using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Web.Endpoints;

/// <summary>
/// Backing API for the SwitchBot Settings page. Unlike the demo/webhook endpoints in
/// this project (which call <c>.DisableAntiforgery()</c> because they accept
/// anonymous or non-browser traffic), every mutating endpoint here binds its
/// parameters via <c>[FromForm]</c> and deliberately does NOT call
/// <c>.DisableAntiforgery()</c>, so ASP.NET Core's antiforgery middleware
/// (registered by <c>AddRazorComponents()</c> and enabled by <c>app.UseAntiforgery()</c>
/// in Program.cs) automatically rejects a request whose antiforgery token is missing
/// or invalid before the handler runs.
///
/// Authorization follows this project's existing convention (see
/// <c>DeviceSyncEndpoints</c>): a manual <see cref="ICurrentUserAccessor.Current"/> +
/// household-ownership check, rather than the framework's <c>RequireAuthorization()</c>,
/// because the zero-config dev/demo mode's "signed in" user
/// (<c>DevCurrentUserAccessor</c>) is intentionally not an ASP.NET Core
/// authenticated <c>ClaimsPrincipal</c> -- <c>RequireAuthorization()</c> would 401
/// every request in that mode. Only a Household Owner may view or change their
/// household's SwitchBot connection; every response is checked to never include the
/// plaintext Token/Secret.
/// </summary>
public static class SwitchBotConnectionEndpoints
{
    public static IEndpointRouteBuilder MapSwitchBotConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/switchbot/connection", async (
            Guid householdId,
            ICurrentUserAccessor currentUserAccessor,
            SwitchBotConnectionService connectionService,
            CancellationToken ct) =>
        {
            var forbidden = await RequireOwnerAsync(householdId, currentUserAccessor, connectionService, ct);
            if (forbidden is not null)
            {
                return forbidden;
            }

            var status = await connectionService.GetStatusAsync(householdId, ct);
            return Results.Ok(new
            {
                status = status.Status.ToString(),
                lastValidatedAtUtc = status.LastValidatedAtUtc,
                lastSyncAtUtc = status.LastSyncAtUtc,
                lastErrorMessage = status.LastErrorMessage
            });
        }).WithName("GetSwitchBotConnection");

        app.MapPost("/api/switchbot/connection", async (
            HttpRequest request,
            ICurrentUserAccessor currentUserAccessor,
            SwitchBotConnectionService connectionService,
            CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            if (!Guid.TryParse(form["householdId"], out var householdId))
            {
                return Results.BadRequest(new { error = "householdId is required." });
            }

            var forbidden = await RequireOwnerAsync(householdId, currentUserAccessor, connectionService, ct);
            if (forbidden is not null)
            {
                return forbidden;
            }

            var token = form["token"].ToString();
            var secret = form["secret"].ToString();

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(secret))
            {
                return Results.BadRequest(new { error = "トークンとシークレットの両方を入力してください。" });
            }

            var outcome = await connectionService.ValidateAndSaveAsync(householdId, token, secret, ct);

            return outcome == SwitchBotConnectionSaveOutcome.Saved
                ? Results.Ok(new { saved = true })
                : Results.UnprocessableEntity(new { saved = false, error = "SwitchBotへの接続を確認できませんでした。トークン・シークレットをご確認ください。" });
        }).WithName("PostSwitchBotConnection");

        app.MapPost("/api/switchbot/connection/disconnect", async (
            HttpRequest request,
            ICurrentUserAccessor currentUserAccessor,
            SwitchBotConnectionService connectionService,
            CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            if (!Guid.TryParse(form["householdId"], out var householdId))
            {
                return Results.BadRequest(new { error = "householdId is required." });
            }

            var forbidden = await RequireOwnerAsync(householdId, currentUserAccessor, connectionService, ct);
            if (forbidden is not null)
            {
                return forbidden;
            }

            await connectionService.DisconnectAsync(householdId, ct);
            return Results.Ok(new { disconnected = true });
        }).WithName("PostSwitchBotConnectionDisconnect");

        app.MapPost("/api/switchbot/connection/sync", async (
            HttpRequest request,
            ICurrentUserAccessor currentUserAccessor,
            SwitchBotConnectionService connectionService,
            IHouseholdSwitchBotClientFactory clientFactory,
            IAppDbContext db,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            if (!Guid.TryParse(form["householdId"], out var householdId))
            {
                return Results.BadRequest(new { error = "householdId is required." });
            }

            var forbidden = await RequireOwnerAsync(householdId, currentUserAccessor, connectionService, ct);
            if (forbidden is not null)
            {
                return forbidden;
            }

            // Household-scoped provider, resolved fresh for this single sync call --
            // never the ambient DataSourceAwareDeviceProvider/global SwitchBotOptions
            // path, so this always syncs with THIS household's own stored credentials.
            var provider = await clientFactory.GetDeviceProviderAsync(householdId, ct);
            if (!provider.IsConfigured)
            {
                return Results.UnprocessableEntity(new { error = "SwitchBotが未接続のため同期できません。" });
            }

            var syncService = new DeviceSyncService(db, provider, clock);
            var result = await syncService.SyncAsync(householdId, ct: ct);
            await connectionService.MarkSyncedAsync(householdId, ct);

            return Results.Ok(new
            {
                added = result.Added,
                updated = result.Updated,
                deactivated = result.Deactivated,
                totalChanges = result.TotalChanges
            });
        }).WithName("PostSwitchBotConnectionSync");

        return app;
    }

    /// <summary>
    /// Returns a 401/403 result when the caller is anonymous or not this household's
    /// Owner; null when allowed. Internal (rather than private) so it can be unit
    /// tested directly without standing up a full HTTP test host.
    /// </summary>
    internal static async Task<IResult?> RequireOwnerAsync(
        Guid householdId,
        ICurrentUserAccessor currentUserAccessor,
        SwitchBotConnectionService connectionService,
        CancellationToken ct)
    {
        var user = currentUserAccessor.Current;
        if (user is null)
        {
            return Results.Json(new { error = "サインインが必要です。" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!await connectionService.IsOwnerAsync(householdId, user.AppUserId, ct))
        {
            return Results.Json(new { error = "このご家庭のSwitchBot設定を変更する権限がありません。" }, statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }
}

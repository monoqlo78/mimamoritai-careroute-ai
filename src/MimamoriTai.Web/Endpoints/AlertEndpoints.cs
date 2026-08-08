using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Application;
using MimamoriTai.Infrastructure.Data;

namespace MimamoriTai.Web.Endpoints;

public sealed record EvaluateAlertRequest(Guid? HouseholdId);

/// <summary>
/// Manual trigger for the watch/risk alert evaluation, so it can be demonstrated on
/// stage without waiting for the background poll. Uses the same WatchAlertService
/// (and therefore the same dedup/cooldown rules) as the automatic poller.
/// </summary>
public static class AlertEndpoints
{
    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/alerts/evaluate", async (
            EvaluateAlertRequest? request,
            AppDbContext db,
            HouseholdAccessService householdAccess,
            WatchAlertService alerts,
            CancellationToken ct) =>
        {
            var householdId = request?.HouseholdId
                ?? await householdAccess.ResolveDefaultAsync(ct);

            if (householdId is null || householdId == Guid.Empty)
            {
                return Results.NotFound(new { error = "No household is registered." });
            }

            if (!await householdAccess.CanAccessAsync(householdId.Value, ct))
            {
                return Results.Json(new { error = "このご家庭のデータにアクセスする権限がありません。" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var outcome = await alerts.EvaluateAsync(householdId.Value, ct);

            return Results.Ok(new
            {
                status = outcome.Status.ToString(),
                sent = outcome.Sent,
                suppressed = outcome.Suppressed,
                message = outcome.Message,
                riskLevel = outcome.Risk?.Level.ToString(),
                score = outcome.Risk?.Score,
                reason = outcome.Risk?.Reason,
                lineSuccess = outcome.SendResult?.Success,
                lineError = outcome.SendResult?.Error
            });
        }).WithName("PostAlertsEvaluate").DisableAntiforgery();

        return app;
    }
}

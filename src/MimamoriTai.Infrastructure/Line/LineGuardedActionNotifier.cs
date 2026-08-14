using Microsoft.Extensions.Logging;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Infrastructure.Line;

/// <summary>
/// Tells the whole household, over LINE, that a heating appliance was switched on from
/// away.
///
/// <para>
/// Modelled on WatchAlertService.PushToAllAsync: every recipient is attempted, one
/// failure never stops the rest, and nothing is thrown back at the caller. The appliance
/// is already on by the time we get here, so the family must not be shown an error that
/// makes them think the command failed and retry it.
/// </para>
/// </summary>
public sealed class LineGuardedActionNotifier(
    ILineRecipientResolver recipientResolver,
    ILineMessagingClient line,
    ILogger<LineGuardedActionNotifier> logger) : IGuardedActionNotifier
{
    public async Task NotifyAsync(GuardedActionNotice notice, CancellationToken ct = default)
    {
        IReadOnlyList<string> recipients;
        try
        {
            recipients = await recipientResolver.ResolveAsync(notice.HouseholdId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve LINE recipients for guarded action broadcast.");
            return;
        }

        if (recipients.Count == 0)
        {
            logger.LogInformation("Guarded action on {Device} had no LINE recipients to notify.", notice.DeviceName);
            return;
        }

        var where = string.IsNullOrWhiteSpace(notice.RoomName) ? string.Empty : $"（{notice.RoomName}）";
        var how = notice.Source switch
        {
            CommandSource.Line => "LINEから",
            CommandSource.Web => "アプリから",
            _ => "遠隔で"
        };

        var card = new LineAlertCard(
            "遠隔でONにしました",
            $"{notice.DeviceName}{where} を{how}ONにしました。\n"
            + $"時刻: {TimeZoneInfo.ConvertTimeBySystemTimeZoneId(notice.OccurredAtUtc, "Tokyo Standard Time"):M月d日 HH:mm}\n"
            + "火や熱をあつかう機器のため、ご家族全員にお知らせしています。\n"
            + "近くにいる方は、周囲に燃えやすいものがないか確認をお願いします。",
            "注意");

        foreach (var to in recipients)
        {
            try
            {
                await line.PushAlertAsync(to, card, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Guarded action broadcast failed for one recipient; continuing with the rest.");
            }
        }
    }
}

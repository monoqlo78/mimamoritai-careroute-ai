using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>Result of handling a single rich-menu postback tap.</summary>
public sealed record LinePostbackOutcome(string ReplyText, int RecipientsNotified);

/// <summary>
/// Handles the fixed set of "one-touch" rich menu postback actions (助けて / 体調が悪い /
/// 大丈夫 / 今日の様子 / 家族に連絡) so the webhook endpoint stays a thin dispatcher instead
/// of a large switch. Each action replies to the tapping user and, where
/// appropriate, pushes a plain factual notice to every *other* active LINE recipient in the
/// household — it never makes a medical diagnosis/claim, only reports that a button was
/// tapped, and it never assigns a resident/family role to the tapping LINE user (LineRecipient
/// rows are not role-aware today).
/// </summary>
public sealed class LinePostbackActionService(
    IAppDbContext db,
    ILineMessagingClient line,
    ILineRecipientResolver recipientResolver,
    IFabricDataAgentClient fabric,
    ILocalDataQuestionService localData,
    TimeProvider clock)
{
    public const string Emergency = "action=emergency";
    public const string Unwell = "action=unwell";
    public const string Okay = "action=okay";
    public const string Status = "action=status";
    public const string ContactFamily = "action=contact_family";

    private const string StatusQuestion = "今日の様子を教えて";
    private const string FallbackReply = "操作を受け付けました。";
    private static readonly TimeSpan FabricStatusTimeout = TimeSpan.FromSeconds(2);

    public async Task<LinePostbackOutcome> HandleAsync(
        Guid householdId, string? sourceId, string postbackData, CancellationToken ct = default) =>
        postbackData switch
        {
            Emergency => await HandleEmergencyAsync(householdId, sourceId, ct),
            Unwell => await HandleUnwellAsync(householdId, sourceId, ct),
            Okay => await HandleOkayAsync(householdId, ct),
            Status => await HandleStatusAsync(householdId, ct),
            ContactFamily => await HandleContactFamilyAsync(householdId, sourceId, ct),
            _ => new LinePostbackOutcome(FallbackReply, 0)
        };

    private async Task<LinePostbackOutcome> HandleEmergencyAsync(Guid householdId, string? sourceId, CancellationToken ct)
    {
        var residentName = await ResolveResidentNameAsync(householdId, ct);
        var pushText =
            $"【緊急】{WithHonorific(residentName)}が「助けて」ボタンを押しました。({FormatTimestamp()})\n" +
            "すぐに連絡・確認をお願いします。";

        var notified = await PushToOthersAsync(householdId, sourceId, pushText, ct);
        await RecordAsync(householdId, $"[LINE] {WithHonorific(residentName)}が「助けて」ボタンを押しました。", ct);

        var reply = notified > 0
            ? "助けての合図を家族に伝えました。もうすぐ連絡があります。"
            : "助けての合図を記録しました。今すぐ危険なときは119番へ電話してください。";

        return new LinePostbackOutcome(reply, notified);
    }

    private async Task<LinePostbackOutcome> HandleUnwellAsync(Guid householdId, string? sourceId, CancellationToken ct)
    {
        var residentName = await ResolveResidentNameAsync(householdId, ct);
        var pushText =
            $"{WithHonorific(residentName)}が「体調が悪い」と伝えています。({FormatTimestamp()})\n" +
            "様子を確認してください。";

        var notified = await PushToOthersAsync(householdId, sourceId, pushText, ct);
        await RecordAsync(householdId, $"[LINE] {WithHonorific(residentName)}が「体調が悪い」と伝えました。", ct);

        return new LinePostbackOutcome("家族に知らせました。お大事にしてください。", notified);
    }

    private async Task<LinePostbackOutcome> HandleOkayAsync(Guid householdId, CancellationToken ct)
    {
        var residentName = await ResolveResidentNameAsync(householdId, ct);
        await RecordAsync(householdId, $"[LINE] {WithHonorific(residentName)}が「大丈夫」と伝えました。", ct);

        return new LinePostbackOutcome("大丈夫を受け付けました", 0);
    }

    private async Task<LinePostbackOutcome> HandleStatusAsync(Guid householdId, CancellationToken ct)
    {
        FabricAnswer answer;
        if (fabric.IsConfigured)
        {
            using var fabricCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            fabricCts.CancelAfter(FabricStatusTimeout);
            answer = await fabric.AskAsync(StatusQuestion, fabricCts.Token);
        }
        else
        {
            answer = await localData.AnswerAsync(householdId, StatusQuestion, ct);
        }

        if (!answer.Success)
        {
            answer = await localData.AnswerAsync(householdId, StatusQuestion, ct);
        }

        await RecordAsync(householdId, "[LINE] 「今日の様子」を確認しました。", ct);

        return new LinePostbackOutcome(answer.Answer, 0);
    }

    private async Task<LinePostbackOutcome> HandleContactFamilyAsync(Guid householdId, string? sourceId, CancellationToken ct)
    {
        var residentName = await ResolveResidentNameAsync(householdId, ct);
        var pushText =
            $"{WithHonorific(residentName)}が「家族に連絡」を希望しています。({FormatTimestamp()})\n" +
            "お電話などでご連絡ください。";

        var notified = await PushToOthersAsync(householdId, sourceId, pushText, ct);
        await RecordAsync(householdId, $"[LINE] {WithHonorific(residentName)}が家族への連絡を希望しました。", ct);

        return new LinePostbackOutcome("家族に連絡の希望を伝えました。", notified);
    }

    /// <summary>
    /// Pushes to every resolved recipient except the tapping user. Returns the number of
    /// recipients that reported success; PushAsync never throws (both the real and mock
    /// LINE clients catch their own transport errors), so no defensive try/catch is needed
    /// here — a per-recipient failure just isn't counted.
    /// </summary>
    private async Task<int> PushToOthersAsync(Guid householdId, string? sourceId, string text, CancellationToken ct)
    {
        var recipients = await recipientResolver.ResolveAsync(householdId, ct);
        var others = recipients.Where(r => !string.Equals(r, sourceId, StringComparison.Ordinal)).ToList();

        var notified = 0;
        foreach (var to in others)
        {
            var result = await line.PushAsync(to, text, ct);
            if (result.Success)
            {
                notified++;
            }
        }

        return notified;
    }

    private async Task<string> ResolveResidentNameAsync(Guid householdId, CancellationToken ct)
    {
        var resident = await db.People
            .FirstOrDefaultAsync(p => p.HouseholdId == householdId && p.Role == PersonRole.Resident, ct);

        return string.IsNullOrWhiteSpace(resident?.DisplayName) ? "ご本人" : resident.DisplayName;
    }

    private string FormatTimestamp() => HouseholdTime.ToLocal(clock.GetUtcNow()).ToString("M月d日 H:mm") + " JST";

    private static string WithHonorific(string name) =>
        name.EndsWith("さん", StringComparison.Ordinal) ? name : $"{name}さん";

    private async Task RecordAsync(Guid householdId, string content, CancellationToken ct)
    {
        db.FamilyMessages.Add(new FamilyMessage
        {
            HouseholdId = householdId,
            PersonId = null,
            Source = CommandSource.Line,
            MessageType = MessageType.Notice,
            Content = content,
            OccurredAtUtc = clock.GetUtcNow()
        });

        await db.SaveChangesAsync(ct);
    }
}

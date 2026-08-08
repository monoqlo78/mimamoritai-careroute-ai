using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

public sealed record AssistantRequest(
    Guid HouseholdId,
    Guid? PersonId,
    string Message,
    CommandSource Source);

public sealed record AssistantResponse(
    string Reply,
    AssistantIntent Intent,
    string ResolvedModel,
    string Router,
    bool DeviceChanged,
    Guid? DeviceId);

/// <summary>
/// Single entry point for every natural language message, no matter whether it
/// arrives from the Blazor UI, the API or the LINE webhook.
/// </summary>
public sealed class AssistantOrchestrator(
    IAppDbContext db,
    IAiRouterClient ai,
    IDeviceProvider deviceProvider,
    IFabricDataAgentClient fabric,
    ILocalDataQuestionService localData,
    TimeProvider clock)
{
    private const string SystemPrompt = """
        あなたは高齢者見守りサービス「見守り隊 / CareRoute AI」の意図解析エンジンです。
        ユーザーの日本語メッセージを、次のJSONだけで返してください。前後に文章やコードフェンスを付けないこと。

        {
          "intent": "control_device | device_status | query_data | conversation",
          "deviceAlias": "文字列 または null",
          "action": "turn_on | turn_off | toggle | get_status | null",
          "confidence": 0.0,
          "question": "文字列 または null"
        }

        判定基準:
        - 家電を操作したい -> control_device
        - 家電の状態を知りたい -> device_status (action は get_status)
        - 生活データ・様子・活動時間の質問 -> query_data (question に質問文)
        - それ以外の会話 -> conversation
        - 機器が特定できない場合 deviceAlias は null にし、推測しないこと。
        - confidence は 0.0〜1.0 の確信度。
        """;

    private const string RepairPrompt = "JSONとして解析できませんでした。指定したスキーマのJSONオブジェクトのみを、余計な文字なしで返してください。";

    public async Task<AssistantResponse> HandleAsync(AssistantRequest request, CancellationToken ct = default)
    {
        var aliasHint = await BuildAliasHintAsync(request.HouseholdId, ct);

        var messages = new List<AiMessage>
        {
            AiMessage.System(SystemPrompt),
            AiMessage.System($"登録済みの機器: {aliasHint}"),
            AiMessage.User(request.Message)
        };

        var completion = await ai.CompleteAsync(messages, "intent", jsonMode: true, ct);
        await LogAiAsync(request.HouseholdId, "intent", completion, ct);

        var plan = IntentParser.TryParse(completion.Content);

        // One — and only one — repair attempt when the model returns unusable JSON.
        if (plan is null && completion.Success)
        {
            var retryMessages = new List<AiMessage>(messages)
            {
                AiMessage.Assistant(completion.Content),
                AiMessage.User(RepairPrompt)
            };

            var retry = await ai.CompleteAsync(retryMessages, "intent-repair", jsonMode: true, ct);
            await LogAiAsync(request.HouseholdId, "intent-repair", retry, ct);
            plan = IntentParser.TryParse(retry.Content);
            completion = retry;
        }

        if (plan is null)
        {
            return new AssistantResponse(
                "うまく聞き取れませんでした。もう一度、機器の名前やご質問を具体的に教えてください。",
                AssistantIntent.Conversation,
                completion.ResolvedModel,
                completion.Router,
                false,
                null);
        }

        await RecordMessageAsync(request, MessageType.Text, request.Message, ct);

        var response = plan.Intent switch
        {
            AssistantIntent.ControlDevice or AssistantIntent.DeviceStatus =>
                await HandleDeviceAsync(request, plan, completion, ct),
            AssistantIntent.QueryData => await HandleQueryAsync(request, plan, completion, ct),
            _ => await HandleConversationAsync(request, completion, ct)
        };

        await RecordMessageAsync(request, MessageType.AiReply, response.Reply, ct, isAi: true);
        return response;
    }

    private async Task<AssistantResponse> HandleDeviceAsync(
        AssistantRequest request, AssistantPlan plan, AiCompletionResult completion, CancellationToken ct)
    {
        var action = plan.Intent == AssistantIntent.DeviceStatus
            ? DeviceAction.GetStatus
            : plan.Action ?? DeviceAction.GetStatus;

        var control = new DeviceControlService(db, deviceProvider, clock);
        var outcome = await control.ExecuteAsync(
            request.HouseholdId,
            plan.DeviceAlias,
            action,
            plan.Confidence,
            request.Message,
            request.Source,
            request.PersonId,
            completion.ResolvedModel,
            ct);

        return new AssistantResponse(
            outcome.Message,
            plan.Intent,
            completion.ResolvedModel,
            completion.Router,
            outcome.Executed && DeviceSafetyPolicy.IsStateChanging(action),
            outcome.DeviceId);
    }

    private async Task<AssistantResponse> HandleQueryAsync(
        AssistantRequest request, AssistantPlan plan, AiCompletionResult completion, CancellationToken ct)
    {
        var question = string.IsNullOrWhiteSpace(plan.Question) ? request.Message : plan.Question;

        FabricAnswer answer;
        if (fabric.IsConfigured)
        {
            answer = await fabric.AskAsync(question, ct);
            if (!answer.Success)
            {
                answer = await localData.AnswerAsync(request.HouseholdId, question, ct);
            }
        }
        else
        {
            answer = await localData.AnswerAsync(request.HouseholdId, question, ct);
        }

        return new AssistantResponse(
            answer.Answer,
            AssistantIntent.QueryData,
            completion.ResolvedModel,
            completion.Router,
            false,
            null);
    }

    private async Task<AssistantResponse> HandleConversationAsync(
        AssistantRequest request, AiCompletionResult intentCompletion, CancellationToken ct)
    {
        var messages = new List<AiMessage>
        {
            AiMessage.System("あなたは見守りサービスのやさしいアシスタントです。日本語で1〜2文、簡潔に返答してください。"),
            AiMessage.User(request.Message)
        };

        var reply = await ai.CompleteAsync(messages, "conversation", jsonMode: false, ct);
        await LogAiAsync(request.HouseholdId, "conversation", reply, ct);

        var text = reply.Success && !string.IsNullOrWhiteSpace(reply.Content)
            ? reply.Content.Trim()
            : "承知しました。家族にも共有しておきますね。";

        return new AssistantResponse(
            text,
            AssistantIntent.Conversation,
            reply.ResolvedModel,
            reply.Router,
            false,
            null);
    }

    private async Task<string> BuildAliasHintAsync(Guid householdId, CancellationToken ct)
    {
        var devices = await db.Devices
            .Where(d => d.HouseholdId == householdId && d.IsEnabled)
            .Select(d => new { d.Alias, d.Name })
            .ToListAsync(ct);

        return devices.Count == 0
            ? "(なし)"
            : string.Join(", ", devices.Select(d => $"{d.Alias}({d.Name})"));
    }

    private async Task RecordMessageAsync(
        AssistantRequest request, MessageType type, string content, CancellationToken ct, bool isAi = false)
    {
        db.FamilyMessages.Add(new FamilyMessage
        {
            HouseholdId = request.HouseholdId,
            PersonId = isAi ? null : request.PersonId,
            Source = request.Source,
            MessageType = type,
            Content = content,
            OccurredAtUtc = clock.GetUtcNow()
        });

        await db.SaveChangesAsync(ct);
    }

    private async Task LogAiAsync(Guid householdId, string purpose, AiCompletionResult result, CancellationToken ct)
    {
        db.AiRequestLogs.Add(new AiRequestLog
        {
            HouseholdId = householdId,
            Purpose = purpose,
            Router = result.Router,
            ResolvedModel = result.ResolvedModel,
            DurationMs = result.DurationMs,
            Success = result.Success,
            CreatedAtUtc = clock.GetUtcNow()
        });

        await db.SaveChangesAsync(ct);
    }
}

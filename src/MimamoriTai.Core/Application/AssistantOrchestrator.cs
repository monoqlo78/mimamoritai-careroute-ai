using System.Text.RegularExpressions;
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
    Guid? DeviceId,
    bool AwaitingConfirmation = false);

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
    TimeProvider clock,
    IPendingActionStore? pendingActions = null,
    TimeSpan? fabricBudget = null)
{
    private readonly IPendingActionStore _pending = pendingActions ?? new InMemoryPendingActionStore();

    /// <summary>
    /// How long the Fabric Data Agent is allowed to take before the query path gives
    /// up on it and answers from the local database instead.
    ///
    /// Fabric is an enhancement over local data, never a prerequisite: the local
    /// answer is already complete before Fabric is consulted. Measured against the
    /// live workspace a single AskAsync takes ~19s and is then rejected anyway
    /// (the data agent cannot reach its datasource), while the LINE webhook cancels
    /// the whole event after 8s. Left unbounded that turns a perfectly good local
    /// answer into the generic timeout message.
    /// </summary>
    private readonly TimeSpan _fabricBudget = fabricBudget ?? TimeSpan.FromSeconds(4);

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

    /// <summary>
    /// Turns the raw data-agent / local-database answer into something a worried
    /// family member actually wants to read. Deliberately forbids inventing numbers:
    /// the figures must come from the supplied facts only.
    /// </summary>
    private const string SummaryPrompt = """
        あなたは高齢者見守りサービス「見守り隊」のアシスタントです。
        ご家族（離れて暮らす息子・娘）に向けて、データの要約をやさしい日本語で伝えてください。

        ルール:
        - 与えられた「データ」に書かれている事実だけを使い、数値や時刻を創作しないこと。
        - 家電の「台数」は、データに台数が明記されている場合のみ答えること。利用回数など別の
          数値から台数を推測してはならない。明記が無ければ台数には触れないこと。
        - データに数値が書かれている場合は「記録がありません」と答えてはならない。
        - 「[端末の記録から確認できる事実]」が付いている情報は最も信頼できる情報として優先すること。
        - データの一部に「取得できなかった」「技術的な問題」など、集計側の不調を述べる記述が
          混じっていても、それは家族には伝えず無視し、確認できた事実だけを伝えること。
        - データに「記録がありません」「利用がありません」とある場合は、それを事実としてそのまま
          やさしく伝えること。機器の故障・通信エラー・システムの不具合だと決めつけないこと。
        - 2〜3文、120文字程度。専門用語や英語は使わない。
        - 落ち着いた、安心できる語り口にする。過度に不安をあおらない。
        - 心配な兆候がある場合は、最後にひと言だけやさしく声かけを提案する。
        - 箇条書きにせず、自然な文章で書くこと。
        """;

    public async Task<AssistantResponse> HandleAsync(AssistantRequest request, CancellationToken ct = default)
    {
        // A pending proposal is answered before anything is sent to the model: "はい" on
        // its own carries no intent, and re-parsing it would lose the action it refers to.
        var confirmation = await TryResolveConfirmationAsync(request, ct);
        if (confirmation is not null)
        {
            return confirmation;
        }

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

        // Anything that physically changes the home is proposed first and executed only
        // after the family says yes, so a misread message cannot act on its own.
        if (DeviceSafetyPolicy.IsStateChanging(action))
        {
            var proposal = await ProposeAsync(request, plan, action, completion, ct);
            if (proposal is not null)
            {
                return proposal;
            }
        }

        return await ExecuteDeviceAsync(
            request, plan.DeviceAlias, action, plan.Confidence, request.Message, plan.Intent, completion, ct);
    }

    private async Task<AssistantResponse> ExecuteDeviceAsync(
        AssistantRequest request,
        string? alias,
        DeviceAction action,
        double confidence,
        string originalText,
        AssistantIntent intent,
        AiCompletionResult completion,
        CancellationToken ct)
    {
        var control = new DeviceControlService(db, deviceProvider, clock);
        var outcome = await control.ExecuteAsync(
            request.HouseholdId,
            alias,
            action,
            confidence,
            originalText,
            request.Source,
            request.PersonId,
            completion.ResolvedModel,
            ct);

        return new AssistantResponse(
            outcome.Message,
            intent,
            completion.ResolvedModel,
            completion.Router,
            outcome.Executed && DeviceSafetyPolicy.IsStateChanging(action),
            outcome.DeviceId);
    }

    /// <summary>
    /// Turns a state-changing plan into a confirmation question. Returns null when the
    /// request should just run: an unresolvable or unsafe device is better reported by
    /// the control service, which produces the precise reason and audits the attempt.
    /// </summary>
    private async Task<AssistantResponse?> ProposeAsync(
        AssistantRequest request,
        AssistantPlan plan,
        DeviceAction action,
        AiCompletionResult completion,
        CancellationToken ct)
    {
        var devices = await db.Devices
            .Where(d => d.HouseholdId == request.HouseholdId)
            .ToListAsync(ct);

        var matches = DeviceResolver.Resolve(devices, plan.DeviceAlias);

        // Exactly one safe, permitted target is the only case worth confirming.
        if (matches.Count != 1
            || DeviceSafetyPolicy.Validate(matches[0], action, plan.Confidence) is not null)
        {
            return null;
        }

        var device = matches[0];

        _pending.Set(new PendingDeviceAction(
            request.HouseholdId,
            plan.DeviceAlias ?? device.Alias,
            device.DisplayName,
            action,
            request.Message,
            clock.GetUtcNow()));

        var verb = action switch
        {
            DeviceAction.TurnOn => "つけます",
            DeviceAction.TurnOff => "消します",
            _ => "切り替えます"
        };

        return new AssistantResponse(
            $"{device.DisplayName} を{verb}。よろしいですか？（「はい」で実行、「いいえ」で中止）",
            plan.Intent,
            completion.ResolvedModel,
            completion.Router,
            false,
            device.Id,
            AwaitingConfirmation: true);
    }

    /// <summary>
    /// Consumes a yes/no answer to a pending proposal. Returns null when there is nothing
    /// pending, or when the message is not a yes/no, in which case it is a fresh instruction.
    /// </summary>
    private async Task<AssistantResponse?> TryResolveConfirmationAsync(AssistantRequest request, CancellationToken ct)
    {
        var answer = ConfirmationReply.Interpret(request.Message);
        if (answer is null)
        {
            return null;
        }

        var pending = _pending.Take(request.HouseholdId, clock.GetUtcNow());
        if (pending is null)
        {
            return null;
        }

        await RecordMessageAsync(request, MessageType.Text, request.Message, ct);

        // Confirmation is explicit human consent, so the model's original confidence
        // no longer gates it; every other safety check still runs in the control service.
        var completion = new AiCompletionResult(true, string.Empty, "Confirmation", "confirmation/none", 0);

        var response = answer.Value
            ? await ExecuteDeviceAsync(
                request, pending.DeviceAlias, pending.Action, 1.0, pending.OriginalText,
                AssistantIntent.ControlDevice, completion, ct)
            : new AssistantResponse(
                $"{pending.DeviceName} の操作を中止しました。",
                AssistantIntent.ControlDevice,
                completion.ResolvedModel,
                completion.Router,
                false,
                null);

        await RecordMessageAsync(request, MessageType.AiReply, response.Reply, ct, isAi: true);
        return response;
    }

    private async Task<AssistantResponse> HandleQueryAsync(
        AssistantRequest request, AssistantPlan plan, AiCompletionResult completion, CancellationToken ct)
    {
        var question = string.IsNullOrWhiteSpace(plan.Question) ? request.Message : plan.Question;

        // The local database is always consulted, even when Fabric is available.
        //
        // The Fabric Data Agent answers in free text and, when it cannot reach its
        // datasource, apologises with HTTP 200 instead of failing. FabricDataAgentMcpClient
        // catches the common wordings, but a phrase list can never cover everything a
        // language model might say. Carrying the local facts alongside means a missed
        // apology degrades the answer instead of erasing it: the summary still has real
        // times and counts to work from.
        var local = await localData.AnswerAsync(request.HouseholdId, question, ct);
        var answer = local;

        if (fabric.IsConfigured)
        {
            var remote = await TryAskFabricAsync(question, ct);
            if (remote is { Success: true } && !string.IsNullOrWhiteSpace(remote.Answer))
            {
                answer = new FabricAnswer(true, Merge(remote.Answer, local.Answer), remote.Source, null);
            }
        }

        var (reply, summary) = await SummarizeAsync(request, question, answer, ct);

        return new AssistantResponse(
            reply,
            AssistantIntent.QueryData,
            summary?.ResolvedModel ?? completion.ResolvedModel,
            summary?.Router ?? completion.Router,
            false,
            null);
    }

    /// <summary>
    /// Consults the Fabric Data Agent without letting it take the answer down with it.
    ///
    /// By the time this runs the local database has already produced a complete answer,
    /// so anything Fabric does beyond enriching it is pure downside. A data agent that
    /// is slow, throwing or unreachable must therefore degrade to that local answer
    /// rather than propagate: the family gets real times and counts instead of an
    /// error, and callers with their own deadline (the LINE webhook cancels an event
    /// after 8 seconds) still get a reply within it.
    ///
    /// Cancellation requested by the caller is deliberately NOT swallowed -- that means
    /// the caller has given up on the whole request, not just on Fabric.
    /// </summary>
    private async Task<FabricAnswer?> TryAskFabricAsync(string question, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_fabricBudget);

        try
        {
            return await fabric.AskAsync(question, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Budget elapsed: fall through to the local answer.
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Presents the Fabric answer and the locally computed facts as two labelled
    /// sources so the model can reconcile them, rather than silently preferring one.
    /// </summary>
    private static string Merge(string remote, string local)
    {
        var localFacts = local?.Trim() ?? string.Empty;

        return localFacts.Length == 0
            ? remote.Trim()
            : $"{remote.Trim()}\n\n[端末の記録から確認できる事実]\n{localFacts}";
    }

    /// <summary>
    /// Rewrites a factual data answer as a gentle, family-facing Japanese summary.
    ///
    /// The raw answer is always kept as the fallback: if the router is unavailable,
    /// throttled or returns nothing usable, the user still gets the correct facts
    /// rather than an error, which is why this never throws.
    /// </summary>
    private async Task<(string Reply, AiCompletionResult? Completion)> SummarizeAsync(
        AssistantRequest request, string question, FabricAnswer answer, CancellationToken ct)
    {
        var facts = answer.Answer?.Trim() ?? string.Empty;

        if (!answer.Success || facts.Length == 0)
        {
            return (string.IsNullOrEmpty(facts) ? "データを取得できませんでした。少し時間をおいて試してください。" : facts, null);
        }

        var messages = new List<AiMessage>
        {
            AiMessage.System(SummaryPrompt),
            AiMessage.User($"ご家族からの質問: {question}\n\nデータ({answer.Source}):\n{facts}")
        };

        var summary = await ai.CompleteAsync(messages, "summary", jsonMode: false, ct);
        await LogAiAsync(request.HouseholdId, "summary", summary, ct);

        var text = summary.Success ? summary.Content.Trim() : string.Empty;

        // Never let the model replace the facts with nothing.
        if (text.Length == 0)
        {
            return (facts, summary);
        }

        // A summary that states a number the data never contained is worse than no
        // summary at all: the family acts on it. Smaller models do invent counts here
        // ("1回" arriving as "4回"), and no amount of prompting removes that entirely,
        // so the claim is checked against the source before it is allowed out.
        if (InventsNumbers(facts, text))
        {
            return (facts, summary);
        }

        return (text, summary);
    }

    /// <summary>
    /// True when <paramref name="summary"/> asserts a figure that does not appear in
    /// <paramref name="facts"/>. Times are compared whole (14:45 must not be satisfied
    /// by an unrelated "45"), and a rounded figure is accepted -- "約11時間半" from
    /// "11.5時間" is a reasonable retelling, "4回" from "1回" is not.
    /// </summary>
    internal static bool InventsNumbers(string facts, string summary)
    {
        var allowed = NumberPattern.Matches(facts)
            .Select(m => m.Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (Match m in NumberPattern.Matches(summary))
        {
            if (allowed.Contains(m.Value))
            {
                continue;
            }

            // Accept a value the source also supports at lower precision, so that
            // rounding for readability is not treated as invention.
            if (double.TryParse(m.Value, out var claimed)
                && allowed.Any(a => double.TryParse(a, out var source) && IsRoundingOf(source, claimed)))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsRoundingOf(double source, double claimed)
    {
        if (Math.Abs(source - claimed) < 0.0001)
        {
            return true;
        }

        // Within 5% covers "約33ワット" for 32.7W but never turns 1 into 4.
        var scale = Math.Max(Math.Abs(source), 1.0);
        return Math.Abs(source - claimed) / scale <= 0.05;
    }

    /// <summary>
    /// Matches a clock time as one token and any other run of digits (with an optional
    /// decimal part) as another, so the two are never confused for one another.
    /// </summary>
    private static readonly Regex NumberPattern =
        new(@"\d{1,2}:\d{2}|\d+(?:\.\d+)?", RegexOptions.Compiled);

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

    /// <summary>
    /// The alias vocabulary handed to the planning model. The family's own name for a
    /// device is listed alongside the provider label so a request phrased with either one
    /// resolves - the resolver accepts both, and the hint must not narrow that.
    /// </summary>
    private async Task<string> BuildAliasHintAsync(Guid householdId, CancellationToken ct)
    {
        var devices = await db.Devices
            .Where(d => d.HouseholdId == householdId && d.IsEnabled)
            .Select(d => new { d.Alias, d.Name, d.DisplayNameOverride })
            .ToListAsync(ct);

        return devices.Count == 0
            ? "(なし)"
            : string.Join(", ", devices.Select(d => string.IsNullOrWhiteSpace(d.DisplayNameOverride)
                ? $"{d.Alias}({d.Name})"
                : $"{d.Alias}({d.DisplayNameOverride}／{d.Name})"));
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

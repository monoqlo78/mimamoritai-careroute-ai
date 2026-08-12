using System.Diagnostics;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Ai;
using MimamoriTai.Infrastructure.Devices;
using MimamoriTai.Infrastructure.Fabric;

namespace MimamoriTai.Tests;

/// <summary>
/// The two-stage routing the LINE assistant runs: one classification that returns JSON,
/// then a specialist that answers.
///
/// The properties worth defending are not "the router is accurate" — that is the model's
/// job — but the three that break people when they go wrong: the deterministic answers must
/// keep working with no model at all, the whole thing must still fit inside the webhook's
/// 8s budget, and questions a professional owns must never be answered here.
/// </summary>
public class AssistantRouterTests
{
    /// <summary>The LINE webhook cancels an event after this long (WebhookEndpoints).</summary>
    private static readonly TimeSpan LineBudget = TimeSpan.FromSeconds(8);

    private static AssistantOrchestrator Create(TestDb db, IAiRouterClient? ai = null) =>
        new(
            db.Context,
            ai ?? new MockAiRouterClient(),
            new MockDeviceProvider(),
            new MockFabricDataAgentClient(),
            new LocalDataQuestionService(db.Context, TimeProvider.System),
            TimeProvider.System);

    // ---------------------------------------------------------------- stage 1: the router

    /// <summary>
    /// The whole reason the topic rides on the intent JSON instead of being asked for
    /// separately. A second classification call would be another ~1.7s against an 8s budget
    /// that already produced the "しばらくたってからお試しください" complaint once.
    /// </summary>
    [Fact]
    public void The_Router_Is_Carried_On_The_Existing_Intent_Json_Not_A_Second_Call()
    {
        var plan = IntentParser.TryParse(
            """{"intent":"conversation","topic":"expert","deviceAlias":null,"action":null,"confidence":0.9}""");

        Assert.NotNull(plan);
        Assert.Equal(AssistantTopic.Expert, plan!.Topic);
    }

    [Theory]
    [InlineData("faq", AssistantTopic.Faq)]
    [InlineData("expert", AssistantTopic.Expert)]
    [InlineData("emergency", AssistantTopic.Emergency)]
    [InlineData("general", AssistantTopic.General)]
    public void Each_Topic_Reaches_Its_Own_Specialist(string raw, AssistantTopic expected)
    {
        var plan = IntentParser.TryParse($$"""{"intent":"conversation","topic":"{{raw}}","confidence":0.9}""");

        Assert.NotNull(plan);
        Assert.Equal(expected, plan!.Topic);
    }

    /// <summary>
    /// A model that has never seen the topic field — an older deployment, or one that simply
    /// dropped the key — must land exactly where the code landed before the field existed.
    /// </summary>
    [Fact]
    public void A_Missing_Topic_Falls_Back_To_The_Behaviour_That_Shipped_Before_It()
    {
        var plan = IntentParser.TryParse("""{"intent":"conversation","confidence":0.9}""");

        Assert.NotNull(plan);
        Assert.Equal(AssistantTopic.General, plan!.Topic);
    }

    /// <summary>
    /// intent and topic can disagree. The intent is the field that has been in production,
    /// so it wins: a hallucinated "general" must never stop the lights being switched off.
    /// </summary>
    [Theory]
    [InlineData("control_device", "turn_off", AssistantTopic.Device)]
    [InlineData("device_status", "get_status", AssistantTopic.Device)]
    [InlineData("query_data", null, AssistantTopic.Data)]
    public void A_Device_Or_Data_Intent_Is_Never_Overruled_By_The_Topic(
        string intent, string? action, AssistantTopic expected)
    {
        var actionJson = action is null ? "null" : $"\"{action}\"";
        var plan = IntentParser.TryParse(
            $$"""{"intent":"{{intent}}","topic":"general","deviceAlias":"light","action":{{actionJson}},"confidence":0.9}""");

        Assert.NotNull(plan);
        Assert.Equal(expected, plan!.Topic);
    }

    // ------------------------------------------------- stage 2: the expert specialist

    /// <summary>
    /// The exact question that must never be answered here. A model will produce a
    /// confident dosage answer, and an 85 year old will follow it.
    /// </summary>
    [Theory]
    [InlineData("この薬とこの薬を一緒に飲んでいい？")]
    [InlineData("薬をやめてもいいですか")]
    [InlineData("血圧の薬を減らしてもよいでしょうか")]
    [InlineData("副作用が心配です")]
    public async Task Medicine_Questions_Are_Handed_To_A_Person_Not_Answered(string question)
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, question, CommandSource.Line));

        Assert.Contains("お医者さん", response.Reply);
        // Turning someone away is not the goal: the worry is received first, and a route
        // to a human is left open.
        Assert.Contains("家族に連絡", response.Reply);
    }

    [Fact]
    public async Task Care_And_Money_Questions_Reach_Their_Own_Professionals()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        var care = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "要介護の申請はどうすればいいですか", CommandSource.Line));
        Assert.Contains("地域包括支援センター", care.Reply);

        var money = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "年金はいくらもらえますか", CommandSource.Line));
        Assert.Contains("年金事務所", money.Reply);
    }

    /// <summary>
    /// The referral costs no model call, so it survives an outage and never spends the
    /// budget on a question it is not going to answer anyway.
    /// </summary>
    [Fact]
    public async Task An_Expert_Question_Is_Referred_Without_Calling_Any_Model()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var counting = new CountingAiRouterClient();
        var orchestrator = Create(db, counting);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "この薬とこの薬を一緒に飲んでいい？", CommandSource.Line));

        Assert.Equal(0, counting.CallCount);
        Assert.Equal(AssistantOrchestrator.KnowledgeBaseRouter, response.Router);
    }

    [Fact]
    public async Task Expert_Referral_Still_Works_While_The_Router_Is_Down()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db, new DeadAiRouterClient());

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "この薬とこの薬を一緒に飲んでいい？", CommandSource.Line));

        Assert.Contains("お医者さん", response.Reply);
    }

    /// <summary>
    /// 「薬を飲みました」 is a resident telling someone about their day. Answering that with
    /// "わたしからはお答えしません" turns a moment of contact into a rebuff, so the bare word
    /// is not enough on its own.
    /// </summary>
    [Theory]
    [InlineData("薬を飲みました")]
    [InlineData("今日は病院に行ってきました")]
    [InlineData("おはようございます")]
    [InlineData("家族の追加方法は")]
    public void Ordinary_Messages_Are_Not_Turned_Away_As_Expert_Questions(string message)
    {
        Assert.Null(AssistantExpertGuidance.TryRefer(message));
    }

    /// <summary>
    /// The referral must not outrank the emergency route: 「胸が痛い」 needs 119, not a
    /// suggestion to make an appointment.
    /// </summary>
    [Fact]
    public async Task Emergencies_Still_Beat_The_Expert_Referral()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, "胸が痛い、薬を飲んでもいいですか", CommandSource.Line));

        Assert.Contains("119", response.Reply);
        Assert.Contains("助けて", response.Reply);
    }

    /// <summary>
    /// When the keyword list did not name the field but the model still flagged the question
    /// as one for a professional, the fallback refers rather than improvising an answer.
    /// </summary>
    [Fact]
    public void An_Unnamed_Expert_Question_Still_Gets_A_Referral()
    {
        Assert.Contains("お医者さん", AssistantExpertGuidance.General.Reply);
        Assert.Contains("家族に連絡", AssistantExpertGuidance.General.Reply);
    }

    // ------------------------------------------------------------------- the 8s budget

    /// <summary>
    /// The failure this whole design is arranged around. Every representative question has
    /// to answer inside the webhook's budget; the token makes an over-budget answer throw
    /// rather than pass quietly.
    /// </summary>
    [Theory]
    [InlineData("家族の追加方法は")]
    [InlineData("連携コードが使えません")]
    [InlineData("お金はかかりますか")]
    [InlineData("誰が見ているのですか")]
    [InlineData("もうやめたいです")]
    [InlineData("この薬とこの薬を一緒に飲んでいい？")]
    [InlineData("要介護の申請はどうすればいいですか")]
    [InlineData("胸が痛い")]
    [InlineData("ありがとう")]
    public async Task Every_Route_Answers_Inside_The_Line_Budget(string question)
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var orchestrator = Create(db);

        using var cts = new CancellationTokenSource(LineBudget);
        var started = Stopwatch.GetTimestamp();

        var response = await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, question, CommandSource.Line), cts.Token);

        Assert.True(
            Stopwatch.GetElapsedTime(started) < LineBudget,
            $"'{question}' exceeded the {LineBudget.TotalSeconds}s LINE budget.");
        Assert.False(string.IsNullOrWhiteSpace(response.Reply));
    }

    /// <summary>
    /// The budget is spent on at most one classification plus at most one answer. A second
    /// classification round trip is the specific thing this design refuses to do.
    /// </summary>
    [Theory]
    [InlineData("家族の追加方法は", 0)]
    [InlineData("この薬とこの薬を一緒に飲んでいい？", 0)]
    [InlineData("胸が痛い", 0)]
    [InlineData("桜はいつ咲きますか", 2)]
    public async Task No_Question_Costs_More_Than_One_Classification_And_One_Answer(
        string question, int expected)
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var counting = new CountingAiRouterClient();
        var orchestrator = Create(db, counting);

        await orchestrator.HandleAsync(
            new AssistantRequest(db.HouseholdId, null, question, CommandSource.Line));

        Assert.Equal(expected, counting.CallCount);
        Assert.True(counting.CallCount <= 2);
    }

    // ------------------------------------------------------- the answers people receive

    /// <summary>
    /// The reported symptom: asked how to add a family member, the answer opened with
    /// SwitchBot and read as an answer to a different question.
    ///
    /// The screen label cannot be invented — an elderly user hunts the screen for the exact
    /// characters they were given. Since the top bar now carries a 「家族の追加」 link, that is
    /// what the reply quotes; the fix is the frame around it: the reply has to say what is
    /// being done before it says where to tap.
    /// </summary>
    [Fact]
    public void The_Family_Answer_Says_What_It_Is_Doing_Before_It_Names_A_Screen()
    {
        var answer = AssistantKnowledgeBase.TryAnswer("家族の追加方法は", FaqMatchMode.Strict);

        Assert.NotNull(answer);
        var firstLine = answer!.Reply.Split('\n')[0];

        Assert.Contains("ご家族", firstLine);
        // The first thing read must not be the device vendor's name.
        Assert.DoesNotContain("SwitchBot", firstLine);
        // ...and the button it sends them to must be one that exists on the screen.
        Assert.Contains("「家族の追加」", answer.Reply);
        Assert.Contains("「連携コードを発行する」", answer.Reply);
    }

    [Theory]
    [InlineData("連携コードが使えません", "10分")]
    [InlineData("お金はかかりますか", "お金はかかりません")]
    [InlineData("誰が見ているのですか", "ご家族だけ")]
    [InlineData("もうやめたいです", "接続を解除する")]
    public void The_Added_Answers_Say_What_The_Code_Actually_Does(string question, string expected)
    {
        var answer = AssistantKnowledgeBase.TryAnswer(question, FaqMatchMode.Strict);

        Assert.NotNull(answer);
        Assert.Contains(expected, answer!.Reply);
    }

    /// <summary>
    /// Every answer, old and new, still has to fit a phone screen held at arm's length.
    /// </summary>
    [Theory]
    [InlineData("家族の追加方法は")]
    [InlineData("連携コードが使えません")]
    [InlineData("お金はかかりますか")]
    [InlineData("誰が見ているのですか")]
    [InlineData("もうやめたいです")]
    [InlineData("センサーが反応しません")]
    [InlineData("通知が来ない")]
    public void Every_Answer_Stays_Readable_In_A_Single_Line_Bubble(string question)
    {
        var answer = AssistantKnowledgeBase.TryAnswer(question, FaqMatchMode.Loose);

        Assert.NotNull(answer);
        Assert.True(answer!.Reply.Length <= 200, $"'{question}' answer is {answer.Reply.Length} chars, too long for LINE.");
        Assert.DoesNotContain("4.", answer.Reply);
    }

    /// <summary>
    /// 「接続を解除する」「今すぐ同期する」 are quoted verbatim by the new answers, exactly
    /// as the existing label-drift test guards the others. Renaming the button on the screen
    /// without renaming it here sends people looking for a word that is not there.
    /// </summary>
    [Theory]
    [InlineData("接続を解除する")]
    [InlineData("今すぐ同期する")]
    public void Screen_Labels_Quoted_By_The_New_Answers_Still_Exist_On_The_Screen(string label)
    {
        var root = RepoRoot();

        var knowledgeBase = File.ReadAllText(
            Path.Combine(root, "src/MimamoriTai.Core/Application/AssistantKnowledgeBase.cs"));
        Assert.Contains($"「{label}」", knowledgeBase);

        var ui = File.ReadAllText(
            Path.Combine(root, "src/MimamoriTai.Web/Components/Pages/SwitchBotSettings.razor"));
        Assert.Contains(label, ui);
    }

    /// <summary>The prompt has to carry the same refusal the deterministic layer enforces.</summary>
    [Fact]
    public void The_Prompt_Facts_Forbid_Answering_What_A_Professional_Owns()
    {
        Assert.Contains("自分で判断して答えないこと", AssistantKnowledgeBase.ProductFacts);
        Assert.Contains("地域包括支援センター", AssistantKnowledgeBase.ProductFacts);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MimamoriTai.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private sealed class CountingAiRouterClient : IAiRouterClient
    {
        private readonly MockAiRouterClient _inner = new();

        public int CallCount { get; private set; }

        public bool IsConfigured => true;

        public string DisplayName => "CountingRouter";

        public Task<AiCompletionResult> CompleteAsync(
            IReadOnlyList<AiMessage> messages, string purpose, bool jsonMode = false, CancellationToken ct = default)
        {
            CallCount++;
            return _inner.CompleteAsync(messages, purpose, jsonMode, ct);
        }
    }

    /// <summary>Stands in for a router outage: every call fails, nothing is parsable.</summary>
    private sealed class DeadAiRouterClient : IAiRouterClient
    {
        public bool IsConfigured => true;

        public string DisplayName => "DeadRouter";

        public Task<AiCompletionResult> CompleteAsync(
            IReadOnlyList<AiMessage> messages, string purpose, bool jsonMode = false, CancellationToken ct = default) =>
            Task.FromResult(new AiCompletionResult(false, string.Empty, DisplayName, "none", 0, "unavailable"));
    }
}

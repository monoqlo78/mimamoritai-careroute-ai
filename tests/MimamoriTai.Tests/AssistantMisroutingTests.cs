using MimamoriTai.Core.Application;

namespace MimamoriTai.Tests;

/// <summary>
/// Guards two failures found by probing the router with wording a real resident would use,
/// rather than with the wording the rules were written from.
///
/// 1. 「機器の追加方法は」 used to answer with the *family* instructions, because
///    <c>add-family</c> listed a bare ["追加","方法"] pair and wins on list order.
/// 2. Broad subject words (デイサービス / 年金 / 手術 …) used to trigger the professional
///    referral even when the resident was simply reporting their day. Being told
///    「わたしからはお答えしません」 in reply to 「デイサービスに行ってきました」 turns
///    contact they initiated into a rebuff, so those words now need a question in the message.
/// </summary>
public sealed class AssistantMisroutingTests
{
    [Theory]
    // Everyday reports. None of these ask anything, so none may be deflected.
    [InlineData("薬を飲みました")]
    [InlineData("デイサービスに行ってきました")]
    [InlineData("デイサービスの人が来ました")]
    [InlineData("年金が入ったので買い物に行きます")]
    [InlineData("年金の通知が来ました")]
    [InlineData("税金の紙が届いた")]
    [InlineData("老人ホームの話、こないだ聞いたよ")]
    [InlineData("手術の跡はもう痛くないです")]
    [InlineData("手術は無事に終わりました")]
    [InlineData("認知症の番組を見た")]
    [InlineData("認知症の予防に散歩してます")]
    [InlineData("保険金の手続きは終わったよ")]
    [InlineData("保険の営業さんが来た")]
    [InlineData("介護のしごとをしていました")]
    public void DailyReportsAreNotDeflectedToAProfessional(string message)
    {
        Assert.Null(AssistantExpertGuidance.TryRefer(message));
    }

    [Theory]
    // The same subjects, asked as questions. These must still be handed to a professional.
    [InlineData("デイサービスはどうやって申し込むの", ExpertField.Care)]
    [InlineData("老人ホームに入るにはどうしたらいい？", ExpertField.Care)]
    [InlineData("介護保険の申請方法を教えて", ExpertField.Care)]
    [InlineData("要介護の申請はどうすれば", ExpertField.Care)]
    [InlineData("税金はいくらかかりますか", ExpertField.Money)]
    [InlineData("年金はいくらもらえますか", ExpertField.Money)]
    [InlineData("相続のことで困っています", ExpertField.Money)]
    [InlineData("弁護士に相談したほうがいいでしょうか", ExpertField.Money)]
    [InlineData("認知症かどうか心配です", ExpertField.Medical)]
    [InlineData("手術を受けたほうがいいですか", ExpertField.Medical)]
    [InlineData("この薬とこの薬を一緒に飲んでいい？", ExpertField.Medical)]
    [InlineData("飲み合わせが心配です", ExpertField.Medical)]
    public void QuestionsAboutTheSameSubjectsStillRefer(string message, ExpertField expected)
    {
        var referral = AssistantExpertGuidance.TryRefer(message);

        Assert.NotNull(referral);
        Assert.Equal(expected, referral.Field);
    }

    [Theory]
    [InlineData("機器の追加方法は")]
    [InlineData("センサーを追加したい")]
    [InlineData("機器を追加するには")]
    public void AskingAboutDevicesDoesNotAnswerWithFamilyInstructions(string message)
    {
        var hit = AssistantKnowledgeBase.TryAnswer(message, FaqMatchMode.Strict);

        Assert.NotNull(hit);
        Assert.Equal("add-device", hit.Id);
    }

    [Theory]
    [InlineData("家族の追加方法は")]
    [InlineData("家族を追加したい")]
    [InlineData("息子を追加するには")]
    public void AskingAboutFamilyStillAnswersWithFamilyInstructions(string message)
    {
        var hit = AssistantKnowledgeBase.TryAnswer(message, FaqMatchMode.Strict);

        Assert.NotNull(hit);
        Assert.Equal("add-family", hit.Id);
    }
}

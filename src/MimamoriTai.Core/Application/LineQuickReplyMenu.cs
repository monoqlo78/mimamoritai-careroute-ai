using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Core.Application;

/// <summary>
/// The choices offered under a LINE reply.
///
/// Residents rarely know what they are allowed to ask, and a blank text box invites
/// silence rather than questions. Showing the common ones as chips turns the reply
/// itself into the menu, which matters most for 「家族の追加」: it is the first thing
/// a new household needs and the least likely thing to be guessed at unprompted.
///
/// Two kinds of chip, deliberately:
/// message chips send wording the knowledge base answers with no model call, so a
/// tap is answered instantly even while the AI router is down; postback chips reuse
/// the rich-menu actions verbatim, so a tap runs exactly the code the button runs.
/// A chip must never be the only route to anything -- everything here is also
/// reachable by typing, and by the rich menu where an action exists.
/// </summary>
public static class LineQuickReplyMenu
{
    /// <summary>
    /// Attached to assistant answers and to the timeout notice. Kept to five so the
    /// row stays readable on a phone held at arm's length, ordered by how often the
    /// question is actually asked.
    /// </summary>
    public static IReadOnlyList<LineQuickReply> Default { get; } =
    [
        // Sends the full question rather than the label: "家族の追加" alone is a
        // command-shaped fragment, while "家族の追加方法は" is unambiguously a
        // question and matches the knowledge base before any model runs.
        LineQuickReply.Message("家族の追加", "家族の追加方法は"),
        LineQuickReply.Message("通知が来ない", "通知が来ない"),
        LineQuickReply.Message("使い方", "使い方"),
        LineQuickReply.Postback("今日の様子", LinePostbackActionService.Status),
        LineQuickReply.Postback("家族に連絡", LinePostbackActionService.ContactFamily)
    ];
}

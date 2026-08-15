using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Core.Application;

/// <summary>Why a relayed exchange did or did not reach LINE.</summary>
public enum LineRelayOutcome
{
    /// <summary>Delivered to at least one recipient.</summary>
    Sent,

    /// <summary>No channel credentials, so the app is running against the demo mock.</summary>
    NotConfigured,

    /// <summary>Configured, but nobody in this household has added the bot as a friend.</summary>
    NoRecipient,

    /// <summary>LINE rejected every push.</summary>
    Failed
}

/// <param name="Outcome">What happened.</param>
/// <param name="Delivered">How many recipients actually received it.</param>
public sealed record LineRelayResult(LineRelayOutcome Outcome, int Delivered)
{
    /// <summary>A short line the dashboard can show the family when nothing was sent.</summary>
    public string? Explanation => Outcome switch
    {
        LineRelayOutcome.NotConfigured => "LINEの接続情報が未設定のため、この画面の中だけのやり取りです。",
        LineRelayOutcome.NoRecipient => "LINEの送り先がまだありません。QRコードから友だち追加してください。",
        LineRelayOutcome.Failed => "LINEへの送信に失敗しました。時間をおいて試してください。",
        _ => null
    };
}

/// <summary>
/// Mirrors a question asked on the dashboard's LINE panel, and the assistant's answer,
/// into the family's actual LINE conversation.
///
/// This exists because the panel used to be a rehearsal and nothing more: it ran the
/// question through the assistant and appended both sides to the page, which reads
/// exactly like a real send and yet never touched LINE. The webhook direction worked
/// (a message typed in LINE is answered in LINE), so the gap was invisible until
/// someone sent from the browser and waited for a phone that was never going to buzz.
///
/// Kept separate from the webhook path on purpose. There the reply token ties the
/// answer to the incoming message; here there is no token, so the exchange has to be
/// pushed, and a push can fail for reasons the family can act on -- no credentials, or
/// nobody has added the bot yet. Returning why, rather than a bool, is what lets the
/// dashboard say so instead of silently doing nothing again.
/// </summary>
public sealed class LineConversationRelay(
    ILineMessagingClient client,
    ILineRecipientResolver recipients)
{
    public async Task<LineRelayResult> RelayAsync(
        Guid householdId,
        string asker,
        string question,
        string reply,
        CancellationToken ct = default)
    {
        if (!client.IsConfigured)
        {
            return new LineRelayResult(LineRelayOutcome.NotConfigured, 0);
        }

        var to = await recipients.ResolveAsync(householdId, ct);
        if (to.Count == 0)
        {
            return new LineRelayResult(LineRelayOutcome.NoRecipient, 0);
        }

        var text = Compose(asker, question, reply);

        // Sent one at a time, and a rejection for one recipient is not allowed to cost
        // the others their message: a family member who blocked the bot must not
        // silence everybody else.
        var delivered = 0;
        foreach (var id in to)
        {
            var result = await client.PushAsync(id, text, ct);
            if (result.Success)
            {
                delivered++;
            }
        }

        return delivered > 0
            ? new LineRelayResult(LineRelayOutcome.Sent, delivered)
            : new LineRelayResult(LineRelayOutcome.Failed, 0);
    }

    /// <summary>
    /// Sends a message the family typed on the dashboard straight to the household's LINE.
    ///
    /// Distinct from <see cref="RelayAsync"/>: nothing is asked of the assistant and no
    /// answer is generated. The panel this serves is labelled "家族にLINEで送る", and the
    /// only honest implementation of that label is a push. It previously ran the text
    /// through the assistant and printed the reply on the page -- which duplicated the
    /// 見守りAI card directly above it and, worse, delivered nothing to anyone's phone.
    /// </summary>
    public async Task<LineRelayResult> SendAsync(
        Guid householdId,
        string sender,
        string message,
        CancellationToken ct = default)
    {
        if (!client.IsConfigured)
        {
            return new LineRelayResult(LineRelayOutcome.NotConfigured, 0);
        }

        var to = await recipients.ResolveAsync(householdId, ct);
        if (to.Count == 0)
        {
            return new LineRelayResult(LineRelayOutcome.NoRecipient, 0);
        }

        var text = ComposeMessage(sender, message);

        var delivered = 0;
        foreach (var id in to)
        {
            var result = await client.PushAsync(id, text, ct);
            if (result.Success)
            {
                delivered++;
            }
        }

        return delivered > 0
            ? new LineRelayResult(LineRelayOutcome.Sent, delivered)
            : new LineRelayResult(LineRelayOutcome.Failed, 0);
    }

    /// <summary>
    /// Names the sender. A message arriving in a shared family talk with no attribution
    /// leaves the resident guessing which of their children wrote it.
    /// </summary>
    public static string ComposeMessage(string sender, string message)
    {
        var who = string.IsNullOrWhiteSpace(sender) ? "ご家族" : sender.Trim();
        return $"{who}さんからのメッセージ\n{message.Trim()}";
    }

    /// <summary>
    /// Carries the question across with the answer. On the dashboard the question is
    /// still on screen above the reply; in LINE it would arrive alone, and an answer
    /// with no question is a puzzle rather than a reassurance.
    /// </summary>
    public static string Compose(string asker, string question, string reply)
    {
        var who = string.IsNullOrWhiteSpace(asker) ? "ご家族" : asker.Trim();
        return $"{who}さんからの質問\n「{question.Trim()}」\n\n{reply.Trim()}";
    }
}

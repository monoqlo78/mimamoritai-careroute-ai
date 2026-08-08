namespace MimamoriTai.Core.Abstractions;

public sealed record FabricAnswer(bool Success, string Answer, string Source, string? Error = null);

/// <summary>
/// Fabric Data Agent abstraction. Today this is answered from local data; once the
/// Fabric Data Agent is published the MCP-backed implementation is registered instead.
/// </summary>
public interface IFabricDataAgentClient
{
    bool IsConfigured { get; }
    Task<FabricAnswer> AskAsync(string question, CancellationToken ct = default);
}

/// <summary>Answers a limited set of life-rhythm questions directly from the app database.</summary>
public interface ILocalDataQuestionService
{
    Task<FabricAnswer> AnswerAsync(Guid householdId, string question, CancellationToken ct = default);
}

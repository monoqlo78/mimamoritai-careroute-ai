using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// DEMO ONLY. Stands in for the Fabric Data Agent while Fabric is not configured.
/// It reports IsConfigured = false so the orchestrator transparently falls back to
/// <see cref="ILocalDataQuestionService"/>.
/// </summary>
public sealed class MockFabricDataAgentClient : IFabricDataAgentClient
{
    public bool IsConfigured => false;

    public Task<FabricAnswer> AskAsync(string question, CancellationToken ct = default) =>
        Task.FromResult(new FabricAnswer(false, string.Empty, "MockFabric", "Fabric Data Agent is not configured."));
}

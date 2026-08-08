namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// Microsoft Fabric Data Agent settings.
/// No secrets here: authentication uses Azure.Identity (DefaultAzureCredential /
/// Azure CLI login locally, Managed Identity in Azure).
/// </summary>
public sealed class FabricOptions
{
    public const string SectionName = "Fabric";

    /// <summary>Scope requested for Fabric API access tokens.</summary>
    public const string DefaultScope = "https://api.fabric.microsoft.com/.default";

    public bool Enabled { get; set; }

    public string WorkspaceId { get; set; } = string.Empty;

    public string DataAgentId { get; set; } = string.Empty;

    /// <summary>MCP endpoint published by the Fabric Data Agent.</summary>
    public string McpUrl { get; set; } = string.Empty;

    public string Scope { get; set; } = DefaultScope;

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(McpUrl)
        && !string.IsNullOrWhiteSpace(WorkspaceId)
        && !string.IsNullOrWhiteSpace(DataAgentId);
}

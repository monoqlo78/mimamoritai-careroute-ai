namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// Microsoft Fabric Data Agent settings.
/// Authentication: when <see cref="TenantId"/>, <see cref="ClientId"/>, and
/// <see cref="ClientSecret"/> are all supplied (see <see cref="HasServicePrincipalCredentials"/>),
/// a normal Entra service principal (client credentials) is used, because Fabric Data
/// Agent query auth does not support managed identities. Otherwise DefaultAzureCredential
/// is used (Azure CLI login locally, Managed Identity in Azure) for local dev / fallback.
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

    /// <summary>
    /// Entra tenant id for the service principal used to authenticate Fabric Data Agent
    /// queries. Optional: leave blank to fall back to DefaultAzureCredential.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Entra application (client) id for the service principal used to authenticate
    /// Fabric Data Agent queries. Optional: leave blank to fall back to DefaultAzureCredential.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Entra client secret for the service principal used to authenticate Fabric Data
    /// Agent queries. Optional: leave blank to fall back to DefaultAzureCredential.
    /// Store this in user-secrets or an environment variable, never in appsettings.json.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// True only when <see cref="TenantId"/>, <see cref="ClientId"/>, and
    /// <see cref="ClientSecret"/> are all non-blank, in which case a
    /// <c>ClientSecretCredential</c> is used instead of DefaultAzureCredential.
    /// </summary>
    public bool HasServicePrincipalCredentials =>
        !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(McpUrl)
        && !string.IsNullOrWhiteSpace(WorkspaceId)
        && !string.IsNullOrWhiteSpace(DataAgentId);
}

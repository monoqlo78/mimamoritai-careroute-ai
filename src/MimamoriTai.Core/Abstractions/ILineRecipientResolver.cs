namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// Resolves the LINE push targets ("to" values) for a household's watch alerts.
/// Implemented in Infrastructure (DB-backed) so Core stays free of an EF Core dependency.
/// </summary>
public interface ILineRecipientResolver
{
    /// <summary>
    /// Returns the user/group ids to push an alert to. An explicit LineOptions.AlertToId
    /// always wins (backwards compatible with a manually configured target); otherwise the
    /// household's active, self-registered LINE recipients are returned. Empty when neither
    /// is available — the caller must still evaluate/record the alert, just not push it.
    /// </summary>
    Task<IReadOnlyList<string>> ResolveAsync(Guid householdId, CancellationToken ct = default);
}

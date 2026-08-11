using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>Safe-to-display snapshot of a household's SwitchBot connection. Never includes the secret.</summary>
public sealed record SwitchBotConnectionStatusView(
    SwitchBotConnectionStatus Status,
    DateTimeOffset? LastValidatedAtUtc,
    DateTimeOffset? LastSyncAtUtc,
    string? LastErrorMessage);

public enum SwitchBotConnectionSaveOutcome
{
    Saved,
    ValidationFailed,
}

/// <summary>
/// Application service backing the SwitchBot Settings UI/API: validates a
/// household owner's Token/Secret against the real SwitchBot API before saving,
/// stores only encrypted values, and never returns plaintext credentials once
/// saved. Ownership checks (only a Household Owner may configure/disconnect) live
/// here so both the Blazor page and the minimal-API endpoints share one policy.
/// </summary>
public sealed class SwitchBotConnectionService(
    IAppDbContext db,
    ICredentialProtector protector,
    IHouseholdSwitchBotClientFactory clientFactory,
    TimeProvider clock)
{
    /// <summary>Only a Household Owner may view/change this household's SwitchBot connection.</summary>
    public async Task<bool> IsOwnerAsync(Guid householdId, Guid appUserId, CancellationToken ct = default) =>
        await db.HouseholdMembers.AnyAsync(
            m => m.HouseholdId == householdId && m.AppUserId == appUserId && m.Role == HouseholdMemberRole.Owner, ct);

    /// <summary>Safe-to-display status; never exposes the encrypted or plaintext credential values.</summary>
    public async Task<SwitchBotConnectionStatusView> GetStatusAsync(Guid householdId, CancellationToken ct = default)
    {
        var connection = await db.SwitchBotConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.HouseholdId == householdId, ct);

        return connection is null
            ? new SwitchBotConnectionStatusView(SwitchBotConnectionStatus.NotConfigured, null, null, null)
            : new SwitchBotConnectionStatusView(connection.Status, connection.LastValidatedAtUtc, connection.LastSyncAtUtc, connection.LastErrorMessage);
    }

    /// <summary>
    /// Validates the given Token/Secret with a real <c>GET /v1.1/devices</c> call
    /// before persisting anything. On success, encrypts and upserts the household's
    /// <see cref="SwitchBotConnection"/> row; on failure, nothing is written except
    /// (if a row already existed) a non-secret <see cref="SwitchBotConnection.LastErrorMessage"/>.
    /// The raw token/secret parameters are never logged and are discarded once this
    /// method returns.
    /// </summary>
    public async Task<SwitchBotConnectionSaveOutcome> ValidateAndSaveAsync(
        Guid householdId, string token, string secret, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var validClient = clientFactory.CreateAdHocClient(token, secret);
        var validated = await TryValidateAsync(validClient, ct);

        var connection = await db.SwitchBotConnections.FirstOrDefaultAsync(c => c.HouseholdId == householdId, ct);

        if (!validated.Success)
        {
            if (connection is not null)
            {
                connection.Status = SwitchBotConnectionStatus.Error;
                connection.LastErrorMessage = validated.SafeErrorMessage;
                connection.UpdatedAtUtc = now;
                await db.SaveChangesAsync(ct);
            }

            return SwitchBotConnectionSaveOutcome.ValidationFailed;
        }

        if (connection is null)
        {
            connection = new SwitchBotConnection
            {
                HouseholdId = householdId,
                CreatedAtUtc = now
            };
            db.SwitchBotConnections.Add(connection);
        }

        connection.EncryptedToken = protector.Protect(token);
        connection.EncryptedSecret = protector.Protect(secret);
        connection.Status = SwitchBotConnectionStatus.Connected;
        connection.LastValidatedAtUtc = now;
        connection.LastErrorMessage = null;
        connection.UpdatedAtUtc = now;

        await db.SaveChangesAsync(ct);
        return SwitchBotConnectionSaveOutcome.Saved;
    }

    /// <summary>Removes the household's stored credentials entirely (no soft-disable state).</summary>
    public async Task DisconnectAsync(Guid householdId, CancellationToken ct = default)
    {
        var connection = await db.SwitchBotConnections.FirstOrDefaultAsync(c => c.HouseholdId == householdId, ct);
        if (connection is not null)
        {
            db.SwitchBotConnections.Remove(connection);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Stamps LastSyncAtUtc after a caller-driven manual "sync now" completes successfully.</summary>
    public async Task MarkSyncedAsync(Guid householdId, CancellationToken ct = default)
    {
        var connection = await db.SwitchBotConnections.FirstOrDefaultAsync(c => c.HouseholdId == householdId, ct);
        if (connection is not null)
        {
            connection.LastSyncAtUtc = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task<(bool Success, string? SafeErrorMessage)> TryValidateAsync(ISwitchBotClient client, CancellationToken ct)
    {
        if (!client.IsConfigured)
        {
            return (false, "トークンまたはシークレットが未入力です。");
        }

        try
        {
            await client.GetDeviceListRawAsync(ct);
            return (true, null);
        }
        catch (HttpRequestException)
        {
            // Never include the token/secret or any response body in the message.
            return (false, "SwitchBot APIへの接続に失敗しました。トークン・シークレットをご確認ください。");
        }
        catch (TaskCanceledException)
        {
            return (false, "SwitchBot APIへの接続がタイムアウトしました。");
        }
    }
}

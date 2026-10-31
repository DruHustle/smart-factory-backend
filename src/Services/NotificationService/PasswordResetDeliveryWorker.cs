using Npgsql;
using SmartFactory.BuildingBlocks.DashboardAccess;

namespace SmartFactory.Services.NotificationService;

public sealed class PasswordResetDeliveryWorker(
    DashboardStore store, IServiceScopeFactory scopes, PasswordResetDeliveryOptions options,
    ILogger<PasswordResetDeliveryWorker> logger, TimeProvider? clock = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delaySeconds = 5;
        string? outage = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
                var delivered = await DeliverOne(sender, stoppingToken);
                outage = null;
                delaySeconds = delivered ? 1 : Math.Min(30, delaySeconds * 2);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
            {
                if (outage != "migration") logger.LogInformation("Password reset delivery is waiting for its database migration.");
                outage = "migration";
                delaySeconds = 30;
            }
            catch
            {
                // Never log a queue row, payload, reset URL, recipient or provider exception.
                if (outage != "queue") logger.LogWarning("Password reset queue unavailable; delivery will retry.");
                outage = "queue";
                delaySeconds = 30;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), clock ?? TimeProvider.System, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task<bool> DeliverOne(IEmailSender sender, CancellationToken ct)
    {
        var configured = options.Configured && sender.Configured;
        // The statement claims one row with a two-minute lease, then releases its
        // connection before mail I/O. Never lock a user after locking its token;
        // password consumption consistently locks the user first in the API.
        await using var claim = store.Source.CreateCommand("""
            UPDATE password_reset_tokens SET next_attempt_at=now()+interval '2 minutes',
              attempts=attempts+CASE WHEN $1 AND used_at IS NULL AND expires_at>now() THEN 1 ELSE 0 END
            WHERE id=(SELECT id FROM password_reset_tokens WHERE delivery_payload<>'' AND (
                used_at IS NOT NULL OR expires_at<=now() OR
                (attempts<4 AND next_attempt_at<=now() AND
                  (delivery_status IN ('pending','failed') OR ($1 AND delivery_status='unconfigured'))))
              ORDER BY expires_at,id FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING id,user_id,email,token_hash,auth_version,delivery_payload,expires_at,used_at,attempts,next_attempt_at
            """);
        claim.Parameters.AddWithValue(configured);
        PasswordResetDelivery delivery;
        await using (var reader = await claim.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return false;
            delivery = new(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7), reader.GetInt32(8),
                reader.GetFieldValue<DateTimeOffset>(9));
        }
        var now = DateTimeOffset.UtcNow;
        PasswordResetAccount? account = null;
        if (delivery.UsedAt is null && delivery.ExpiresAt > now)
        {
            await using var current = store.Source.CreateCommand("""
                SELECT id,"openId",email,auth_version,disabled_at FROM users WHERE id=$1
                """);
            current.Parameters.AddWithValue(delivery.UserId);
            await using var reader = await current.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                account = new(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
        }
        var rejection = PasswordResetDeliveryPolicy.Rejection(delivery, account, DateTimeOffset.UtcNow);
        if (rejection is not null)
        {
            await Finish(delivery, rejection == "expired" ? "expired" : "blocked", rejection, clear: true, delay: 0, ct);
            return true;
        }
        if (!configured)
        {
            await Finish(delivery, "unconfigured", "mail_not_configured", clear: false, delay: 30, ct);
            return true;
        }
        if (!sender.Allows(delivery.Email))
        {
            await Finish(delivery, "blocked", "recipient_not_allowed", clear: true, delay: 0, ct);
            return true;
        }
        if (!options.TryDecryptLink(delivery, out var resetUrl))
        {
            await Finish(delivery, "blocked", "invalid_delivery_payload", clear: true, delay: 0, ct);
            return true;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await sender.Send(delivery.Email, "Reset your Smart Factory password",
                PasswordResetDeliveryPolicy.Body(resetUrl!, delivery.ExpiresAt),
                PasswordResetDeliveryPolicy.ProviderIdentity(delivery.Id), timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            await Finish(delivery, "failed", "delivery_failed", clear: delivery.Attempts >= PasswordResetDeliveryPolicy.MaximumAttempts,
                delay: Math.Min(300, 30 * (1 << Math.Min(delivery.Attempts, 3))), ct);
            return true;
        }
        await Finish(delivery, "accepted", null, clear: true, delay: 0, ct);
        return true;
    }

    private async Task Finish(PasswordResetDelivery delivery, string state, string? error, bool clear, int delay, CancellationToken ct)
    {
        // A successful reset or a newer queue claim can never be overwritten by
        // an older sender finishing late. Keep the one-time hash and expiry intact.
        await using var update = store.Source.CreateCommand("""
            UPDATE password_reset_tokens SET delivery_status=$2,last_error=$3,
              delivery_payload=CASE WHEN $4 THEN '' ELSE delivery_payload END,
              next_attempt_at=now()+make_interval(secs => $5)
            WHERE id=$1 AND attempts=$6 AND next_attempt_at=$7 AND delivery_payload=$8
            """);
        update.Parameters.AddWithValue(delivery.Id);
        update.Parameters.AddWithValue(state);
        update.Parameters.AddWithValue((object?)error ?? DBNull.Value);
        update.Parameters.AddWithValue(clear);
        update.Parameters.AddWithValue(delay);
        update.Parameters.AddWithValue(delivery.Attempts);
        update.Parameters.AddWithValue(delivery.LeaseUntil);
        update.Parameters.AddWithValue(delivery.Payload);
        await update.ExecuteNonQueryAsync(ct);
    }
}

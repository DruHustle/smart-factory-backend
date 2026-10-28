using Npgsql;
using SmartFactory.BuildingBlocks.DashboardAccess;
namespace SmartFactory.Services.NotificationService;

public sealed class NotificationDeliveryWorker(DashboardStore store, IServiceScopeFactory scopes, ILogger<NotificationDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delaySeconds = 5;
        while (!stoppingToken.IsCancellationRequested) {
            try {
                using var scope = scopes.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
                if (await DeliverOne(sender, stoppingToken)) { delaySeconds = 5; continue; }
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Notification queue unavailable; delivery will retry."); }
            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            delaySeconds = Math.Min(30, delaySeconds * 2);
        }
    }
    public async Task<bool> DeliverOne(IEmailSender sender, CancellationToken ct)
    {
        // Atomic claim with an expiring lease survives restarts. Other workers skip claimed rows.
        const string claim = """
        UPDATE notification_inbox SET "emailStatus"='processing', "nextAttemptAt"=now()+interval '2 minutes', attempts=attempts+1
        WHERE id=(SELECT id FROM notification_inbox WHERE
          ("emailStatus" IN ('pending','retrying','processing') OR ($1 AND "emailStatus"='unconfigured'))
          AND "nextAttemptAt"<=now() ORDER BY id FOR UPDATE SKIP LOCKED LIMIT 1)
        RETURNING id, "userId", kind, title, body, attempts
        """;
        await using var command = store.Source.CreateCommand(claim);
        command.Parameters.AddWithValue(sender.Configured);
        long id; int userId; string kind; string title; string body; int attempts;
        await using (var reader = await command.ExecuteReaderAsync(ct)) {
            if (!await reader.ReadAsync(ct)) return false;
            id = reader.GetInt64(0); userId = reader.GetInt32(1); kind = reader.GetString(2); title = reader.GetString(3); body = reader.GetString(4); attempts = reader.GetInt32(5);
        }
        var user = await store.GetUser(userId, ct);
        var state = "accepted"; string? failure = null;
        if (!sender.Configured) state = "unconfigured";
        else if (user is null ||
            (kind == "infrastructure" ? user.Role != "admin" :
                kind != "account_welcome" && !new[] { "engineer", "admin" }.Contains(user.Role)) ||
            !sender.Allows(user.Email)) {
            state = "no_recipient"; failure = "Current account role or recipient domain is not authorized for email.";
        } else {
            try {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                await sender.Send(user.Email!, title, body, id, timeout.Token);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) {
                state = attempts >= 8 ? "failed" : "retrying";
                // Never persist SDK exception bodies, recipient addresses, credentials or tokens.
                failure = error is HttpRequestException http && http.StatusCode.HasValue
                    ? sender.Provider + " HTTP " + (int)http.StatusCode.Value
                    : sender.Provider + " delivery request failed or timed out.";
            }
        }
        await using var update = store.Source.CreateCommand("""
        UPDATE notification_inbox SET "emailStatus"=$2, "lastError"=$3,
          "nextAttemptAt"=now()+make_interval(secs => $4), "acceptedAt"=CASE WHEN $2='accepted' THEN now() ELSE NULL END
        WHERE id=$1
        """);
        update.Parameters.AddWithValue(id); update.Parameters.AddWithValue(state);
        update.Parameters.AddWithValue((object?)failure ?? DBNull.Value);
        update.Parameters.AddWithValue(Math.Min(3600, 30 * (1 << Math.Min(attempts, 7))));
        await update.ExecuteNonQueryAsync(ct);
        return true;
    }
}

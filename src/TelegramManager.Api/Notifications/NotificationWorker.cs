using System.Net.Http.Json;
using System.Text.Json;
using MySqlConnector;

namespace TelegramManager.Api.Notifications;

/// <summary>Opt-in delivery from notification_outbox; no retries after an ambiguous network send.</summary>
public sealed class NotificationWorker(MySqlDataSource database, IHttpClientFactory clients, ILogger<NotificationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("REKLIO_NOTIFICATIONS_ENABLED") != "true")
        {
            log.LogInformation("Reklio notification delivery is disabled");
            return;
        }
        var token = Environment.GetEnvironmentVariable("BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("BOT_TOKEN missing");
        using var http = clients.CreateClient();
        while (!ct.IsCancellationRequested)
        {
            try { await ProcessOneAsync(http, token, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Notification delivery cycle failed"); }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private async Task ProcessOneAsync(HttpClient http, string token, CancellationToken ct)
    {
        await using var conn = await database.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var select = new MySqlCommand("""
            SELECT id,recipient_id,body FROM notification_outbox
            WHERE delivery_state='Pending'
              AND (delivery_next_at IS NULL OR delivery_next_at<=UTC_TIMESTAMP())
            ORDER BY id LIMIT 1 FOR UPDATE
            """, conn, tx);
        long id, recipient;
        string body;
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) { await tx.RollbackAsync(ct); return; }
            id = reader.GetInt64(0);
            recipient = reader.GetInt64(1);
            body = reader.GetString(2);
        }
        await using var claim = new MySqlCommand("""
            UPDATE notification_outbox
            SET delivery_state='Sending',delivery_claimed_at=UTC_TIMESTAMP(),
                delivery_attempts=delivery_attempts+1
            WHERE id=@id AND delivery_state='Pending'
            """, conn, tx);
        claim.Parameters.AddWithValue("@id", id);
        if (await claim.ExecuteNonQueryAsync(ct) != 1) { await tx.RollbackAsync(ct); return; }
        await tx.CommitAsync(ct);

        // A timeout after Telegram accepts the message is ambiguous.
        // Keep such deliveries in NeedsReview instead of blindly resending.
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/sendMessage",
                new { chat_id = recipient, text = body }, ct);
            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var ok = response.IsSuccessStatusCode &&
                     json.RootElement.TryGetProperty("ok", out var value) && value.GetBoolean();
            if (!ok)
            {
                var code = json.RootElement.TryGetProperty("error_code", out var e) ? e.GetInt32() : (int)response.StatusCode;
                if (code == 429 || code >= 500)
                {
                    await UpdateAsync(conn, id, "Pending", "Telegram temporarily unavailable", true);
                    return;
                }
                await UpdateAsync(conn, id, "Failed", "Telegram rejected delivery (check bot start/permissions)", false);
                return;
            }
            await UpdateAsync(conn, id, "Sent", null, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Notification {NotificationId} needs delivery review", id);
            await UpdateAsync(conn, id, "NeedsReview", "Delivery result unknown", false);
        }
    }

    private static async Task UpdateAsync(MySqlConnection conn, long id, string state, string? error, bool retry)
    {
        await using var cmd = new MySqlCommand("""
            UPDATE notification_outbox
            SET delivery_state=@state,delivery_last_error=@error,
                delivery_sent_at=IF(@state='Sent',UTC_TIMESTAMP(),delivery_sent_at),
                delivery_next_at=IF(@retry=1,DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 MINUTE),NULL)
            WHERE id=@id AND delivery_state='Sending'
            """, conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@state", state);
        cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@retry", retry ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }
}

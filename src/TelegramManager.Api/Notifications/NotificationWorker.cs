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
            SELECT id,recipient_id,body,order_id FROM notification_outbox
            WHERE state='Pending'
              AND (next_attempt_at IS NULL OR next_attempt_at<=UTC_TIMESTAMP())
            ORDER BY id LIMIT 1 FOR UPDATE
            """, conn, tx);
        long id, recipient, orderId;
        string body;
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) { await tx.RollbackAsync(ct); return; }
            id = reader.GetInt64(0);
            recipient = reader.GetInt64(1);
            body = reader.GetString(2);
            orderId = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
        }
        await using var claim = new MySqlCommand("""
            UPDATE notification_outbox
            SET state='Sending',attempts=attempts+1
            WHERE id=@id AND state='Pending'
            """, conn, tx);
        claim.Parameters.AddWithValue("@id", id);
        if (await claim.ExecuteNonQueryAsync(ct) != 1) { await tx.RollbackAsync(ct); return; }
        await tx.CommitAsync(ct);

        // A timeout after Telegram accepts the message is ambiguous.
        // Keep such deliveries in NeedsReview instead of blindly resending.
        try
        {
            // Every incoming request notification gets actions, regardless of optional order metadata.
            var isIncomingRequest = orderId > 0 &&
                body.StartsWith("Новая заявка", StringComparison.OrdinalIgnoreCase);
            if (isIncomingRequest)
            {
                try
                {
                    await using var details = new MySqlCommand("""
                        SELECT c.title,a.content_text,a.price_rub,a.scheduled_at,a.delete_after_hours
                        FROM ad_requests a LEFT JOIN channels c ON c.telegram_id=a.channel_id
                        WHERE a.id=@order AND a.owner_id=@owner LIMIT 1
                        """, conn);
                    details.Parameters.AddWithValue("@order", orderId);
                    details.Parameters.AddWithValue("@owner", recipient);
                    await using var row = await details.ExecuteReaderAsync(ct);
                    if (await row.ReadAsync(ct))
                    {
                        var channel = row.IsDBNull(0) ? "Канал" : row.GetString(0);
                        var message = row.IsDBNull(1) ? "" : row.GetString(1);
                        var price = row.IsDBNull(2) ? 0m : row.GetDecimal(2);
                        var at = row.IsDBNull(3) ? "не указана" : row.GetDateTime(3).AddHours(3).ToString("dd.MM.yyyy HH:mm") + " (UTC+3)";
                        var duration = row.IsDBNull(4) ? "—" : row.GetInt32(4).ToString();
                        if (message.Length > 650) message = message[..650] + "…";
                        body = $"📥 Новая заявка #{orderId} на рекламу\\n" +
                               $"📺 Канал: {channel}\\n💰 Стоимость: {price:0.##} ₽\\n" +
                               $"📅 Публикация: {at}\\n⏳ Срок: {duration} ч\\n\\n" +
                               $"📝 Текст: {message}\\n\\nВыберите действие:";
                    }
                }
                catch (MySqlException ex)
                {
                    log.LogWarning(ex, "Unable to enrich owner notification {Id}; sending with decision buttons", id);
                }
            }
            object payload = isIncomingRequest
                ? new
                {
                    chat_id = recipient,
                    text = body,
                    reply_markup = (object)new
                    {
                        inline_keyboard = new[]
                        {
                            new[] { new { text = "✅ Принять заявку", callback_data = $"confirm:0:{orderId}" } },
                            new[] { new { text = "❌ Отклонить заявку", callback_data = $"decision:Rejected:{orderId}" } }
                        }
                    }
                }
                : new { chat_id = recipient, text = body, reply_markup = (object?)null };
            log.LogInformation("Notification {Id}: order={OrderId}, owner actions={HasActions}", id, orderId, isIncomingRequest);
            using var response = await http.PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/sendMessage", payload, ct);
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
            SET state=@state,last_error=@error,
                next_attempt_at=IF(@retry=1,DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 MINUTE),next_attempt_at)
            WHERE id=@id AND state='Sending'
            """, conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@state", state);
        cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@retry", retry ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }
}

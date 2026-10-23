using System.Net.Http.Json;
using System.Text.Json;
using MySqlConnector;

namespace TelegramManager.Api.Publishing;

/// <summary>
/// Single-claim delivery worker. Ambiguous Telegram responses are intentionally
/// NOT retried automatically: doing so could duplicate a paid advertisement.
/// Enabled only when REKLIO_PUBLISH_ENABLED=true.
/// </summary>
public sealed class PublicationWorker(MySqlDataSource database, IHttpClientFactory clients, ILogger<PublicationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Environment.GetEnvironmentVariable("REKLIO_PUBLISH_ENABLED") != "true")
        {
            log.LogInformation("Reklio publication worker is disabled");
            return;
        }

        var token = Environment.GetEnvironmentVariable("BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("BOT_TOKEN missing");
        var allowTest = Environment.GetEnvironmentVariable("REKLIO_ALLOW_TEST_PUBLICATIONS") == "true";
        var testChannelIdText = Environment.GetEnvironmentVariable("REKLIO_TEST_CHANNEL_ID");
        long? testChannelId = long.TryParse(testChannelIdText, out var parsedChannelId) && parsedChannelId < 0 ? parsedChannelId : null;
        if (allowTest && testChannelId is null)
            throw new InvalidOperationException("REKLIO_TEST_CHANNEL_ID must be a negative Telegram channel ID when test publishing is enabled");
        log.LogInformation("Publication worker enabled; test-order publication permitted: {AllowTest}", allowTest);
        using var http = clients.CreateClient();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishOneAsync(http, token, allowTest, testChannelId, stoppingToken);
                await DeleteOneAsync(http, token, testChannelId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Reklio publication cycle failed"); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private async Task PublishOneAsync(HttpClient http, string token, bool allowTest, long? testChannelId, CancellationToken ct)
    {
        await using var conn = await database.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Lock the candidate order and claim exactly that order in the same transaction.
        await using var candidate = new MySqlCommand("""
            SELECT a.id,a.channel_id,a.content_text,a.delete_after_hours
            FROM ad_requests a
            WHERE a.status='Scheduled' AND a.scheduled_at<=UTC_TIMESTAMP()
              AND (@testChannelId IS NULL OR a.channel_id=@testChannelId)
              AND EXISTS (
                SELECT 1 FROM reklio_payment_attempts p
                WHERE p.order_id=a.id AND p.status='Succeeded'
                  AND (p.mode='live' OR (p.mode='test' AND @allowTest=1))
              )
              AND NOT EXISTS (SELECT 1 FROM reklio_publications j WHERE j.order_id=a.id)
            ORDER BY a.scheduled_at ASC LIMIT 1 FOR UPDATE
            """, conn, tx);
        candidate.Parameters.AddWithValue("@allowTest", allowTest ? 1 : 0);
        candidate.Parameters.AddWithValue("@testChannelId", (object?)testChannelId ?? DBNull.Value);
        long id, channel;
        string message;
        int? hours = null;
        bool found;
        await using (var reader = await candidate.ExecuteReaderAsync(ct))
        {
            found = await reader.ReadAsync(ct);
            if (found)
            {
                id = reader.GetInt64(0);
                channel = reader.GetInt64(1);
                message = reader.GetString(2);
                hours = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            }
        }
        // Dispose the reader before rolling back or issuing another command.
        if (!found) { await tx.RollbackAsync(ct); return; }
        await using var claim = new MySqlCommand("""
            INSERT INTO reklio_publications(order_id,channel_id,state,delete_after_hours)
            VALUES (@id,@channel,'Claimed',@hours)
            """, conn, tx);
        claim.Parameters.AddWithValue("@id", id);
        claim.Parameters.AddWithValue("@channel", channel);
        claim.Parameters.AddWithValue("@hours", (object?)hours ?? DBNull.Value);
        await claim.ExecuteNonQueryAsync(ct);
        await using var transition = new MySqlCommand("""
            UPDATE ad_requests SET status='Publishing' WHERE id=@id AND status='Scheduled';
            UPDATE reklio_publications SET state='Sending' WHERE order_id=@id AND state='Claimed';
            """, conn, tx);
        transition.Parameters.AddWithValue("@id", id);
        await transition.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);

        // After this point any network failure is ambiguous; require manual reconciliation.
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/sendMessage",
                new { chat_id = channel, text = message, disable_web_page_preview = true }, ct);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!response.IsSuccessStatusCode || !json.RootElement.GetProperty("ok").GetBoolean())
                throw new InvalidOperationException("Telegram rejected sendMessage");
            var messageId = json.RootElement.GetProperty("result").GetProperty("message_id").GetInt64();
            await using var done = new MySqlCommand("""
                UPDATE reklio_publications
                  SET state='Published',telegram_message_id=@message,published_at=UTC_TIMESTAMP()
                WHERE order_id=@id AND state='Sending';
                UPDATE ad_requests SET status='Published' WHERE id=@id AND status='Publishing';
                """, conn);
            done.Parameters.AddWithValue("@id", id);
            done.Parameters.AddWithValue("@message", messageId);
            await done.ExecuteNonQueryAsync(ct);
            await using var notice = new MySqlCommand("""
                INSERT IGNORE INTO notification_outbox (recipient_id,order_id,body)
                SELECT advertiser_id,id,CONCAT('Реклама по заказу #',id,' опубликована.')
                FROM ad_requests WHERE id=@id
                UNION ALL
                SELECT owner_id,id,CONCAT('Реклама по заказу #',id,' опубликована в вашем канале.')
                FROM ad_requests WHERE id=@id
                """, conn);
            notice.Parameters.AddWithValue("@id", id);
            try { await notice.ExecuteNonQueryAsync(ct); }
            catch (MySqlException ex) { log.LogWarning(ex, "Published order {OrderId}, but notification enqueue failed", id); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogError(ex, "Publication {OrderId} is ambiguous; manual reconciliation required", id);
            await using var error = new MySqlCommand("""
                UPDATE reklio_publications SET state='NeedsReview',last_error='Telegram send result unknown'
                WHERE order_id=@id AND state='Sending'
                """, conn);
            error.Parameters.AddWithValue("@id", id);
            await error.ExecuteNonQueryAsync(CancellationToken.None);
            await QueueReviewNoticeAsync(conn, id, "Публикация", log);
        }
    }

    private async Task DeleteOneAsync(HttpClient http, string token, long? testChannelId, CancellationToken ct)
    {
        await using var conn = await database.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var select = new MySqlCommand("""
            SELECT order_id,channel_id,telegram_message_id
            FROM reklio_publications
            WHERE state='Published' AND deleted_at IS NULL
              AND (@testChannelId IS NULL OR channel_id=@testChannelId)
              AND delete_after_hours>0
              AND DATE_ADD(published_at, INTERVAL delete_after_hours HOUR)<=UTC_TIMESTAMP()
            ORDER BY published_at LIMIT 1 FOR UPDATE
            """, conn, tx);
        select.Parameters.AddWithValue("@testChannelId", (object?)testChannelId ?? DBNull.Value);
        long id, channel, message;
        bool found;
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            found = await reader.ReadAsync(ct);
            if (found)
            {
                id = reader.GetInt64(0); channel = reader.GetInt64(1); message = reader.GetInt64(2);
            }
        }
        // MySqlConnector cannot rollback a transaction while a reader is active.
        if (!found) { await tx.RollbackAsync(ct); return; }
        await using var claim = new MySqlCommand("""
            UPDATE reklio_publications SET state='Deleting' WHERE order_id=@id AND state='Published'
            """, conn, tx);
        claim.Parameters.AddWithValue("@id", id);
        await claim.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/deleteMessage",
                new { chat_id = channel, message_id = message }, ct);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!response.IsSuccessStatusCode || !json.RootElement.GetProperty("ok").GetBoolean())
                throw new InvalidOperationException("Telegram rejected deleteMessage");
            await using var done = new MySqlCommand("""
                UPDATE reklio_publications SET state='Deleted',deleted_at=UTC_TIMESTAMP()
                WHERE order_id=@id AND state='Deleting'
                """, conn);
            done.Parameters.AddWithValue("@id", id);
            await done.ExecuteNonQueryAsync(ct);
            await using var notice = new MySqlCommand("""
                INSERT IGNORE INTO notification_outbox (recipient_id,order_id,body)
                SELECT advertiser_id,id,CONCAT('Реклама по заказу #',id,' удалена из канала.')
                FROM ad_requests WHERE id=@id
                UNION ALL
                SELECT owner_id,id,CONCAT('Реклама по заказу #',id,' удалена из вашего канала.')
                FROM ad_requests WHERE id=@id
                """, conn);
            notice.Parameters.AddWithValue("@id", id);
            try { await notice.ExecuteNonQueryAsync(ct); }
            catch (MySqlException ex) { log.LogWarning(ex, "Deleted order {OrderId}, but notification enqueue failed", id); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogError(ex, "Deletion of order {OrderId} needs manual review", id);
            await using var error = new MySqlCommand("""
                UPDATE reklio_publications SET state='NeedsDeleteReview',last_error='Telegram deletion result unknown'
                WHERE order_id=@id AND state='Deleting'
                """, conn);
            error.Parameters.AddWithValue("@id", id);
            await error.ExecuteNonQueryAsync(CancellationToken.None);
            await QueueReviewNoticeAsync(conn, id, "Удаление рекламы", log);
        }
    }
    private static async Task QueueReviewNoticeAsync(MySqlConnection conn, long orderId, string operation, ILogger log)
    {
        try
        {
            await using var cmd = new MySqlCommand("""
                INSERT IGNORE INTO notification_outbox (recipient_id,order_id,body)
                SELECT advertiser_id,id,@body FROM ad_requests WHERE id=@id
                UNION ALL
                SELECT owner_id,id,@body FROM ad_requests WHERE id=@id
                """, conn);
            cmd.Parameters.AddWithValue("@id", orderId);
            cmd.Parameters.AddWithValue("@body", operation + " по заказу #" + orderId + " требует проверки. Обратитесь в поддержку.");
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (MySqlException ex)
        {
            log.LogWarning(ex, "Could not queue review notifications for order {OrderId}", orderId);
        }
    }

}

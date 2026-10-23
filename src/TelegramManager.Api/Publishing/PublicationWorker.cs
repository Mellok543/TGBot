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
        using var http = clients.CreateClient();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishOneAsync(http, token, stoppingToken);
                await DeleteOneAsync(http, token, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Reklio publication cycle failed"); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private async Task PublishOneAsync(HttpClient http, string token, CancellationToken ct)
    {
        await using var conn = await database.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Lock the candidate order and claim exactly that order in the same transaction.
        await using var candidate = new MySqlCommand("""
            SELECT a.id,a.channel_id,a.content_text,a.delete_after_hours
            FROM ad_requests a
            WHERE a.status='Scheduled' AND a.scheduled_at<=UTC_TIMESTAMP()
              AND EXISTS (
                SELECT 1 FROM reklio_payment_attempts p
                WHERE p.order_id=a.id AND p.status='Succeeded'
              )
              AND NOT EXISTS (SELECT 1 FROM reklio_publications j WHERE j.order_id=a.id)
            ORDER BY a.scheduled_at ASC LIMIT 1 FOR UPDATE
            """, conn, tx);
        long id, channel;
        string message;
        int? hours;
        await using (var reader = await candidate.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) { await tx.RollbackAsync(ct); return; }
            id = reader.GetInt64(0);
            channel = reader.GetInt64(1);
            message = reader.GetString(2);
            hours = reader.IsDBNull(3) ? null : reader.GetInt32(3);
        }
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
        }
    }

    private async Task DeleteOneAsync(HttpClient http, string token, CancellationToken ct)
    {
        await using var conn = await database.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var select = new MySqlCommand("""
            SELECT order_id,channel_id,telegram_message_id
            FROM reklio_publications
            WHERE state='Published' AND deleted_at IS NULL
              AND delete_after_hours>0
              AND DATE_ADD(published_at, INTERVAL delete_after_hours HOUR)<=UTC_TIMESTAMP()
            ORDER BY published_at LIMIT 1 FOR UPDATE
            """, conn, tx);
        long id, channel, message;
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) { await tx.RollbackAsync(ct); return; }
            id = reader.GetInt64(0); channel = reader.GetInt64(1); message = reader.GetInt64(2);
        }
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
        }
    }
}

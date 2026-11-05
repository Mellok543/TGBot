using MySqlConnector;
using Reklio.Payments;

namespace Reklio.Payments;

/// <summary>Reconciles pending payments without requiring an open Mini App.</summary>
public sealed class TributePaymentWorker(
    MySqlDataSource database, IHttpClientFactory clients, ILogger<TributePaymentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Tribute payment reconciliation failed"); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var key = Environment.GetEnvironmentVariable("TRIBUTE_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return;
        var orders = new List<(Guid Uuid, long UserId, long? ChannelId, int Days, string Product)>();
        await using (var cmd = database.CreateCommand("""
            SELECT order_uuid,user_id,NULL AS channel_id,30 AS days,product
            FROM reklio_tribute_payments WHERE status='pending'
            UNION ALL
            SELECT order_uuid,user_id,channel_id,days,'promotion7'
            FROM reklio_tribute_promotions WHERE status='pending'
            LIMIT 50
            """))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (Guid.TryParse(reader.GetString(0), out var uuid))
                    orders.Add((uuid,reader.GetInt64(1),reader.IsDBNull(2)?null:reader.GetInt64(2),
                        reader.GetInt32(3),reader.GetString(4)));

        var tribute = new TributeShopClient(clients.CreateClient(),key);
        foreach (var order in orders)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                using var result = await tribute.GetOrderStatusAsync(order.Uuid,ct);
                if (!result.RootElement.TryGetProperty("status",out var status) ||
                    !string.Equals(status.GetString(),"paid",StringComparison.OrdinalIgnoreCase)) continue;
                await ApplyAsync(order,ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error,"Could not reconcile Tribute order {OrderUuid}",order.Uuid);
            }
        }
    }

    private async Task ApplyAsync((Guid Uuid,long UserId,long? ChannelId,int Days,string Product) order,CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var table = order.ChannelId.HasValue ? "reklio_tribute_promotions" : "reklio_tribute_payments";
        await using var lockOrder = new MySqlCommand(
            $"SELECT status FROM {table} WHERE order_uuid=@uuid AND user_id=@user FOR UPDATE",connection,transaction);
        lockOrder.Parameters.AddWithValue("@uuid",order.Uuid.ToString("D"));
        lockOrder.Parameters.AddWithValue("@user",order.UserId);
        var state = await lockOrder.ExecuteScalarAsync(ct) as string;
        if (state != "pending") return;
        if (order.ChannelId is long channelId)
        {
            await using var promote = new MySqlCommand("""
                INSERT INTO channel_promotions(channel_id,active_until)
                VALUES(@channel,DATE_ADD(UTC_TIMESTAMP(),INTERVAL @days DAY))
                ON DUPLICATE KEY UPDATE active_until=
                  DATE_ADD(GREATEST(active_until,UTC_TIMESTAMP()),INTERVAL @days DAY)
                """,connection,transaction);
            promote.Parameters.AddWithValue("@channel",channelId);
            promote.Parameters.AddWithValue("@days",order.Days);
            await promote.ExecuteNonQueryAsync(ct);
        }
        else if (order.Product == "pro30")
        {
            await using var pro = new MySqlCommand("""
                UPDATE users SET subscription_until=DATE_ADD(
                    GREATEST(COALESCE(subscription_until,UTC_TIMESTAMP()),UTC_TIMESTAMP()),INTERVAL 30 DAY)
                WHERE telegram_id=@user
                """,connection,transaction);
            pro.Parameters.AddWithValue("@user",order.UserId);
            if (await pro.ExecuteNonQueryAsync(ct)==0) return;
        }
        else return;

        await using var mark = new MySqlCommand(
            $"UPDATE {table} SET status='paid',applied_at=UTC_TIMESTAMP() WHERE order_uuid=@uuid",
            connection,transaction);
        mark.Parameters.AddWithValue("@uuid",order.Uuid.ToString("D"));
        await mark.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Applied Tribute payment {OrderUuid}",order.Uuid);
    }
}

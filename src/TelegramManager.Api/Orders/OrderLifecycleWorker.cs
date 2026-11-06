using MySqlConnector;

namespace Reklio.Orders;

/// <summary>Moves orders forward when nobody acts: expiring missed requests and escalating early deletions.</summary>
public sealed class OrderLifecycleWorker(MySqlDataSource database, ILogger<OrderLifecycleWorker> log) : BackgroundService
{
    private const string Unclaimed = "NOT EXISTS(SELECT 1 FROM reklio_direct_payments p WHERE p.order_id=a.id)";
    private const string Missed = "((a.status='Pending' OR (a.status='AwaitingPayment' AND " + Unclaimed + ")) AND a.scheduled_at<=UTC_TIMESTAMP())";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await ExpireMissedOrdersAsync(stoppingToken);
                await NotifyBuyersOfEarlyDeletionAsync(stoppingToken);
                await EscalateEarlyDeletionsAsync(stoppingToken);
                await DeleteOldImagesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { log.LogError(error, "Order lifecycle cycle failed"); }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    // An unpaid order past its slot can never run, and it must free that slot. A buyer who reported a
    // transfer is kept out of this: they still need the order open to dispute it.
    private async Task ExpireMissedOrdersAsync(CancellationToken ct)
    {
        await using var conn = await database.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Orders that lapsed long ago, before this worker existed, expire without a surprise message.
        await using var notify = new MySqlCommand($"""
            INSERT IGNORE INTO notification_outbox(recipient_id,order_id,body)
            SELECT a.advertiser_id,a.id,CONCAT('⌛ Заявка #',a.id,' истекла',CHAR(10),'Время публикации прошло, а заказ не был оплачен. Вы можете оформить новую заявку.')
            FROM ad_requests a WHERE {Missed} AND a.scheduled_at>UTC_TIMESTAMP()-INTERVAL 7 DAY
            UNION ALL
            SELECT a.owner_id,a.id,CONCAT('⌛ Заявка #',a.id,' истекла',CHAR(10),'Время публикации прошло, а заказ не был оплачен.')
            FROM ad_requests a WHERE {Missed} AND a.scheduled_at>UTC_TIMESTAMP()-INTERVAL 7 DAY
            """, conn, tx);
        await notify.ExecuteNonQueryAsync(ct);
        await using var expire = new MySqlCommand($"""
            UPDATE ad_requests a SET a.status='Expired'
            WHERE {Missed} OR (a.status='Draft' AND a.created_at<UTC_TIMESTAMP()-INTERVAL 1 DAY)
            """, conn, tx);
        var expired = await expire.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        if (expired > 0) log.LogInformation("Expired {Count} missed orders", expired);
    }

    // A closed order no longer needs its picture, except as evidence during the month a dispute may follow.
    private async Task DeleteOldImagesAsync(CancellationToken ct)
    {
        await using var cmd = database.CreateCommand("""
            DELETE i FROM reklio_order_images i JOIN ad_requests a ON a.id=i.order_id
            WHERE i.created_at<UTC_TIMESTAMP()-INTERVAL 30 DAY
              AND a.status IN ('Rejected','Cancelled','Expired','Deleted','DisputeResolved')
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // The monitor tells only the owner. The buyer paid for this post and has to know it is gone.
    private async Task NotifyBuyersOfEarlyDeletionAsync(CancellationToken ct)
    {
        await using var conn = await database.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        const string Unnotified = """
            a.status='EarlyDeletionPending' AND NOT EXISTS(SELECT 1 FROM ad_order_events e
              WHERE e.order_id=a.id AND e.event_type='EarlyDeletionBuyerNotified' AND e.created_at>=
                (SELECT MAX(d.created_at) FROM ad_order_events d WHERE d.order_id=a.id AND d.event_type='EarlyDeletionDetected'))
            """;
        await using var notify = new MySqlCommand($"""
            INSERT IGNORE INTO notification_outbox(recipient_id,order_id,body)
            SELECT a.advertiser_id,a.id,CONCAT('⚠️ Пост удалён раньше срока · заказ #',a.id,CHAR(10),
              'Владелец может опубликовать его заново в течение 24 часов. Вы можете сразу открыть спор в разделе «Заказы».')
            FROM ad_requests a WHERE {Unnotified}
            """, conn, tx);
        await notify.ExecuteNonQueryAsync(ct);
        await using var record = new MySqlCommand($"""
            INSERT INTO ad_order_events(order_id,actor_id,event_type,details)
            SELECT a.id,NULL,'EarlyDeletionBuyerNotified',NULL FROM ad_requests a WHERE {Unnotified}
            """, conn, tx);
        await record.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    // An owner who ignores a deleted post must not leave the buyer waiting forever.
    private async Task EscalateEarlyDeletionsAsync(CancellationToken ct)
    {
        await using var conn = await database.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        const string Overdue = """
            a.status='EarlyDeletionPending' AND
              (SELECT MAX(d.created_at) FROM ad_order_events d WHERE d.order_id=a.id AND d.event_type='EarlyDeletionDetected')
              <UTC_TIMESTAMP()-INTERVAL 24 HOUR
            """;
        await using var notify = new MySqlCommand($"""
            INSERT IGNORE INTO notification_outbox(recipient_id,order_id,body)
            SELECT a.advertiser_id,a.id,CONCAT('⚖️ Открыт спор · заказ #',a.id,CHAR(10),'Владелец не восстановил удалённый пост за 24 часа. Решение примет администрация Reklio.') FROM ad_requests a WHERE {Overdue}
            UNION ALL
            SELECT a.owner_id,a.id,CONCAT('⚖️ Открыт спор · заказ #',a.id,CHAR(10),'Вы не восстановили удалённый пост за 24 часа. Решение примет администрация Reklio.') FROM ad_requests a WHERE {Overdue}
            UNION ALL
            SELECT 992964625,a.id,CONCAT('⚖️ Новый спор · заказ #',a.id,CHAR(10),'Откройте панель модерации Reklio.') FROM ad_requests a WHERE {Overdue}
            """, conn, tx);
        await notify.ExecuteNonQueryAsync(ct);
        await using var dispute = new MySqlCommand($"""
            UPDATE ad_requests a SET a.status='Disputed',a.payout_status='Blocked',a.disputed_at=UTC_TIMESTAMP(),
              a.dispute_reason='Владелец не восстановил досрочно удалённый пост за 24 часа'
            WHERE {Overdue}
            """, conn, tx);
        await dispute.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
}

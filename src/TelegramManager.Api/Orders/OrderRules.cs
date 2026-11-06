namespace Reklio.Orders;

/// <summary>Order rules shared by several queries. Each fragment expects ad_requests to be aliased as "a".</summary>
public static class OrderRules
{
    /// <summary>When an advertiser may open a dispute. Keep in step with the Mini App's order flow.</summary>
    public const string Disputable = """
        a.payout_status NOT IN ('Paid','Refunded') AND (
          (a.status IN ('Published','Deleted','DeleteFailed','EarlyDeletionPending','PublishFailed')
             AND a.payment_status IN ('Paid','TestPaid','DirectPaid'))
          OR (a.status IN ('Scheduled','Publishing') AND a.payment_status='DirectPaid'
             AND a.scheduled_at<UTC_TIMESTAMP()-INTERVAL 1 HOUR)
          OR (a.status IN ('PaidReschedule','PendingPaid','Cancelled') AND a.payment_status='DirectPaid')
          OR (a.status='AwaitingPayment' AND EXISTS(SELECT 1 FROM reklio_direct_payments p WHERE p.order_id=a.id
             AND (p.status='Rejected' OR (p.status='BuyerClaimed' AND p.claimed_at<UTC_TIMESTAMP()-INTERVAL 24 HOUR))))
        )
        """;
}

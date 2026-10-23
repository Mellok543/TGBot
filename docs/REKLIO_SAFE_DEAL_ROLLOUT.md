# Reklio: YooKassa Safe Deal rollout

## Payment model
- One seller per advertising order.
- Create a YooKassa `safe_deal` with `fee_moment=deal_closed`.
- Pay via `POST /v3/payments` with `deal.id` and `deal.settlements` defining seller payout.
- Store `provider_deal_id`, immutable gross/fee/seller amounts and persisted idempotency keys in `reklio_safe_deals`.
- After payment reconciliation mark the order as paid and allow scheduled Telegram publication.
- After the paid advertising period ends, verify the placement and ensure there is no unresolved dispute.
- Pay seller through `POST /v3/payouts` referencing the deal; use YooKassa-hosted tokenization widget for bank-card payout details.
- Confirm payout status from provider before marking the seller as paid.
- On failure or cancellation: request a provider refund according to Safe Deal rules. Never mark a refund successful before provider confirmation.

## Activation prerequisites
1. Confirm Safe Deal is enabled on Reklio's YooKassa merchant contract (ordinary payment credentials alone are insufficient).
2. Confirm receipt/merchant obligations, supported sellers, commission, and payment-processing expenses.
3. Configure `YOOKASSA_SHOP_ID`, `YOOKASSA_SECRET_KEY` and a percentage `REKLIO_COMMISSION_PERCENT` with sufficient margin for acquiring fees.
4. Apply `deploy/migrations/003_reklio_safe_deals.sql` with a database backup before using Safe Deal endpoints.
5. Confirm the configured deal lifetime covers booking lead time plus placement duration, or restrict future bookings.
6. Implement payout onboarding via YooKassa-hosted card widget and API payout lifecycle. Never collect or save raw PAN/CVV in Reklio.

## Current deployment state
- Existing `/api/orders/{id}/payment` remains *test-only* and does not create a Safe Deal.
- The Safe Deal client has idempotent create and lookup helpers; they are not yet wired to production routes.
- The migration stores immutable settlement snapshots and future payout references; it must not be interpreted as proof of transferred funds.
- No automated real-money payouts are activated by this change.

## Provider docs
- https://yookassa.ru/developers/solutions-for-platforms/safe-deal/basics
- https://yookassa.ru/developers/solutions-for-platforms/safe-deal/integration/deals
- https://yookassa.ru/developers/solutions-for-platforms/safe-deal/integration/payments
- https://yookassa.ru/developers/solutions-for-platforms/safe-deal/integration/payouts

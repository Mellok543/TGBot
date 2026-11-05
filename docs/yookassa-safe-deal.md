# Reklio — YooKassa Safe Deal integration

Status: **foundation only; payments and payouts disabled**. No public payment endpoint is registered.

## Provider product

Use YooKassa **Safe Deal**, not a normal payment plus a promise to hold money.
The Safe Deal product must be explicitly enabled by YooKassa and contractually approved for Reklio.
A two-stage card hold is **not** a substitute: its capture window is limited.

Official docs:
- https://yookassa.ru/developers/solutions-for-platforms/safe-deal/basics
- https://yookassa.ru/developers/solutions-for-platforms/safe-deal/integration/payments
- https://yookassa.ru/developers/solutions-for-platforms/safe-deal/integration/payouts
- https://yookassa.ru/developers/using-api/webhooks

## Planned flow

1. Owner approves order: `Pending -> AwaitingPayment`.
2. Server persists immutable payment snapshot (total, fee, seller payout, order, owner, currency), provider deal ID, and UUID idempotency key **before** any provider request.
3. Server creates a Safe Deal payment, returns only provider confirmation URL to the authenticated advertiser.
4. Webhook is only a trigger: server retrieves payment from YooKassa API using credentials and verifies payment ID, order metadata, amount, currency, paid flag, and status. Update DB transactionally and idempotently.
5. After confirmed payment: `AwaitingPayment -> Scheduled`. Publishing worker must verify schedule, bot permissions and payment state.
6. Store Telegram post ID and evidence of successful publication; keep dispute/refund window.
7. Only after verified fulfillment and dispute rules, initiate provider Safe Deal payout with a **persisted** idempotency key; verify payout result. No payout endpoint for users.
8. Reconcile pending payments/payouts periodically against provider API; log status transitions without secrets.

## Before enabling real money

- Agree with YooKassa on Safe Deal eligibility, legal entity, commission, receipt/fiscalization, refunds, payout methods, recipient onboarding and contract terms.
- Create migrations for `payment_attempts`, `provider_deals`, `payout_attempts`, `payment_events`, unique provider IDs/idempotency keys, and order state transitions.
- Enforce database locks or compare-and-swap for all payment transitions. Never use the browser return URL as payment confirmation.
- Configure credentials via VPS environment/secret manager, never in Git. Protect webhook endpoint, verify via provider API, and restrict provider network ingress where possible.
- Use a separate test shop and test recipients. Perform refund, duplicate webhook, concurrent click, timeout, and recovery tests before production.

## Code

`YooKassaSafeDealClient` is a transport-layer client only. It accepts values from a future trusted application service, not HTTP request bodies. It deliberately has no public controller route or automatic payout method yet.

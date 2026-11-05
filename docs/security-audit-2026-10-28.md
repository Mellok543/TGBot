# Reklio — security audit (static review, 2026-10-28)

Scope: repository Mellok543/TGBot, branch feature/miniapp-typescript-foundation. Read-only code analysis, not a live penetration test. Severity is provisional. Do not infer exploitability without staging validation.

## P1 — Payment endpoint holds database transaction and row locks during external YooKassa request
- File: src/TelegramManager.Api/Program.cs, around lines 1932–2057.
- Evidence: Open transaction, SELECT ... FOR UPDATE, then await client.SendAsync(request, ct), followed by COMMIT.
- Risk: stalled provider/network ties up DB connections and locks; under concurrent load this can degrade order/payment availability. No explicit request timeout visible in this handler.
- Remediation: persist a uniquely keyed payment attempt in a short transaction; commit; call provider with bounded timeout; complete a second transaction with optimistic status transitions and provider idempotency.
- Verification: staging test with delayed provider stub and concurrent payment clicks.

## P1 — No visible app-level request rate limiting
- File: src/TelegramManager.Api/Program.cs, service/middleware configuration around lines 14–102.
- Evidence: no AddRateLimiter/UseRateLimiter or endpoint-specific policies visible in this file.
- Risk: endpoint abuse (auth, catalog, Telegram getChat/getChatMember, avatar proxy, order creation). Reverse-proxy limits, if any, remain unverified.
- Remediation: ASP.NET Core partitioned rate limiting by identity/IP, with distinct policies for channel connection, auth, catalog and payment; independently verify Nginx/WAF rules.
- Verification: controlled staging load testing, confirm 429 and Retry-After without false lockouts.

## P2 — Webhook processing lacks explicit payload size guard in handler
- File: Program.cs, lines 2059–2147.
- Evidence: JsonDocument.ParseAsync(req.Body) without a local body-size cap; no explicit webhook-specific limit identified in this review.
- Risk: excessive memory/CPU consumption by oversized incoming JSON, subject to edge server limits.
- Remediation: set MaxRequestBodySize/size limit and validate Content-Type; preserve provider-side payment verification.
- Verification: staging oversized payload returns 413; legitimate YooKassa notifications still pass.

## P2 — Security headers and deployment hardening need validation
- File: Program.cs, middleware around lines 94–102.
- Evidence: no explicit CSP/HSTS middleware visible here. Nginx may set relevant headers, not audited.
- Remediation: inspect actual HTTPS response headers, configure HSTS, CSP tailored to Mini App, X-Content-Type-Options, Referrer-Policy; verify SSH and MariaDB are not public.
- Verification: inspect deployed config/response and use passive scan.

## Positive security controls observed
- Web authentication: secure, HttpOnly, SameSite=Lax cookie and OIDC PKCE (Program.cs ~35–89).
- Mini App init data: HMAC, constant-time verification and maximum age (Program.cs ~560–595).
- Several web channel/order routes filter by current identity and channel/order ownership (Program.cs ~146–305, 374–492).
- YooKassa callback fetches authoritative payment from provider and checks metadata and exact amount before state transition (Program.cs ~2059–2147). Test mode only.
- SQL queries sampled use parameter binding; no SQL injection demonstrated.

## Follow-up audit checklist
1. Enumerate every /api/admin and /api/orders endpoint for IDOR/BOLA (two isolated staging accounts).
2. Verify callback, cookie and logout CSRF behavior in browser.
3. Test rate limiting at API, Nginx and VPS edge.
4. Review escaped/unescaped rendering of user-controlled ad content and channel names for XSS.
5. Scan dependency manifests, workflow permissions, and Git history for leaked secrets using approved secret scanners; rotate confirmed leaks immediately.
6. Verify DB privileges, backups, access logs and network segmentation.
7. Test YooKassa webhook replay, race conditions and incorrect amounts in test mode.

This report records observations, not confirmed production exploits. Avoid active scans against production without a test plan.

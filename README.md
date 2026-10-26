# Webhook Gateway

![CI](https://github.com/MayhurJoker007/webhook-gateway/actions/workflows/ci.yml/badge.svg)

One place to receive webhooks from outside (payment providers, couriers, ERP callbacks), check they're
genuine, and fan them out to internal services reliably.

Without something like this, every internal service ends up writing its own signature check, its own
duplicate handling and its own retry loop, and each one gets at least one of them slightly wrong.

```
 payment provider ──POST /hooks/payments──┐                      ┌──> orders-service   (payment.*)
 courier          ──POST /hooks/courier───┤  verify → store → 202 ┤
                                          │                      └──> audit-log        (everything)
                                          └── background worker, per-subscriber retries
```

**Stack:** .NET 9 minimal API, EF Core + SQLite, `BackgroundService`, `IHttpClientFactory`, xUnit +
WebApplicationFactory with a fake clock and a fake subscriber.

## Receiving

- **HMAC-SHA256 signatures** over `timestamp.body`, in the header `X-Signature: t=1728900000,v1=<hex>`.
  The comparison is constant time (`CryptographicOperations.FixedTimeEquals`), and several `v1` values are
  accepted, so a sender can rotate its secret without downtime.
- **Replay protection.** Requests whose timestamp is more than 5 minutes off are rejected. Because the timestamp
  is part of what's signed, it can't be swapped for a fresh one.
- **Idempotent.** Senders retry. The same event id from the same source returns `200 {duplicate: true}` and
  isn't stored twice, even if both copies arrive at the same instant (a unique index plus catching the conflict).
- **Fast acknowledgement.** The endpoint only verifies, stores and returns `202`. Calling subscribers happens
  later, so a slow subscriber can't make the sender time out and retry.
- **Body size limit** before anything is parsed.

## Delivering

- Each event and subscriber pair is its own delivery with its own retry state, so one subscriber being
  down doesn't delay the others.
- Outgoing requests are **signed the same way** with the subscriber's secret, and carry `X-Webhook-Id`,
  `X-Webhook-Type` and `X-Webhook-Attempt`.
- **Exponential backoff with jitter**: 10s, 20s, 40s ... capped at an hour.
- **4xx responses aren't retried** (except 408 and 429), because the subscriber rejected the content and
  sending it again won't help. 5xx and timeouts are retried up to `MaxAttempts`.
- `POST /events/{id}/redeliver` requeues failed deliveries once the subscriber is fixed.
- Subscribers pick event types exactly or by prefix (`payment.*`).

## Running

```
dotnet run --project src/WebhookGateway
```

Sources, secrets and subscribers are configured in `appsettings.json` (replace the `change-me` secrets
with environment variables or user-secrets). To send a test event:

```python
import hmac, hashlib, json, time, urllib.request
body = json.dumps({"id": "evt_123", "type": "payment.succeeded", "amount": 1499.00}).encode()
ts = int(time.time())
sig = hmac.new(b"change-me-payments-secret", f"{ts}.".encode() + body, hashlib.sha256).hexdigest()
req = urllib.request.Request("http://localhost:5282/hooks/payments", body,
                             {"X-Signature": f"t={ts},v1={sig}", "Content-Type": "application/json"})
print(urllib.request.urlopen(req).read())
```

`GET /events/{id}` shows each delivery's status, attempts, last HTTP status and next retry time.

## Tests

```
dotnet test
```

They cover a signed event being accepted and routed by type, wrong secrets, unknown sources and missing
headers, a replayed old request, a duplicate event id, outgoing signatures the subscriber can verify,
503s retried on schedule until it gives up, redelivery, 400s not retried, and malformed signature headers.

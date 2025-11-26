# System Architecture

## Services Overview

| Service | Technology | Responsibilities |
| --- | --- | --- |
| Api | ASP.NET Core 10 minimal API | Accepts REST `POST /payments`, validates idempotency, forwards to `PaymentService.Api`. |
| PaymentService.Api | ASP.NET Core 10 Web API + EF Core | Writes payments + outbox rows transactionally, exposes payment read endpoints. |
| PaymentService.Worker | .NET 10 worker | Polls outbox and publishes `payment.created.*` messages, consumes gateway events, updates payment state. |
| PaymentGateway | .NET 10 worker | Simulates external provider; consumes dispatch queue, emits intermediate / final status events. |

## RabbitMQ Topology

| Exchange | Type | Purpose |
| --- | --- | --- |
| `payments.topic` | Topic | Outbound payment lifecycle messages. |
| `gateway.topic` | Topic | Gateway status fan-in. |
| `payments.dlx` | Direct | DLX for retries and poison messages. |

| Queue | Binding | Notes |
| --- | --- | --- |
| `payments.outbound.q` | `payment.created.*` → `payments.topic` | Primary dispatch queue. DLX→`payments.dlx`. |
| `payments.retry.q` | `payments.retry` → `payments.dlx` | TTL 30s, returns to `payments.topic`. |
| `payments.dlq` | `payments.dlx` (catch-all) | Lazy queue for inspection. |
| `gateway.dispatch.q` | `gateway.dispatch.*` → `gateway.topic` | Worker→Gateway dispatch channel. |
| `gateway.intermediate.q` | `gateway.intermediate.*` → `gateway.topic` | Gateway→Worker intermediate events. |
| `gateway.final.q` | `gateway.final.*` → `gateway.topic` | Gateway→Worker final events. |

## PostgreSQL Schema

- `payments` table: payment metadata, status, versioning.
- `payment_status_history`: audit of transitions.
- `outbox`: transactional message store.
- `inbox`: idempotent consumer ledger.

### Key Columns

| Table | Columns |
| --- | --- |
| `payments` | `payment_id`, `client_request_id`, `amount_minor`, `currency_code`, `gateway_code`, `status`, `version`, timestamps. |
| `payment_status_history` | `history_id`, `payment_id`, `previous_status`, `new_status`, `reason_code`, `payload`, `correlation_id`. |
| `outbox` | `outbox_id`, `payment_id`, `event_type`, `payload`, `routing_key`, `processed_utc`, `attempts`, `trace_id`. |
| `inbox` | `inbox_id`, `message_id`, `source_exchange`, `payload_hash`, `processed_utc`. |

## Workflows

1. **REST → Payment Creation**
   1. Api validates payload + `Idempotency-Key`.
   2. Api forwards to `PaymentService.Api`.
   3. `PaymentService.Api` persists payment + outbox row in single transaction.
   4. Api returns 202 with `paymentId`.

2. **Outbox Dispatch**
   1. Worker polls unprocessed outbox rows using `FOR UPDATE SKIP LOCKED` (implemented via EF `OrderBy` + batch). 
   2. Publishes persistent message with confirms; on success stamps `processed_utc`.
   3. On failure increments `attempts` for retry monitoring.

3. **Gateway Simulation**
   1. Gateway consumes dispatch queue.
   2. Emits `gateway.intermediate.*` and later `gateway.final.*` messages with correlation IDs.

4. **Worker Consumption**
   1. Worker consumes intermediate/final queues with manual ack.
   2. Writes inbox row to enforce idempotency.
   3. Updates payment status + history.
   4. Acks message; poison messages route to DLX.

## Observability & Reliability

- Publisher confirms, manual acks, TTL-based retry queues.
- Inbox/outbox tables for consistency.
- Serilog/ILogger instrumentation ready for enrichment.


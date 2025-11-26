# rabbitmq-test
---
## Plan for delelopment
This plan defines a production-grade, local-first .NET 10 microservice system that demonstrates robust RabbitMQ topologies, an Outbox pattern with exactly-once effects in the database and at-least-once delivery on the message bus, idempotent consumers, and resilient error handling with retry/DLX. Docker Compose hosts only infrastructure (RabbitMQ + PostgreSQL); all .NET apps run locally and connect to Docker services via localhost. The design simulates a realistic payment workflow spanning creation, gateway dispatch, intermediate updates, and finalization, with exponential backoff, manual acks, publisher confirms, durable queues, and a well-documented schema and message contracts.

The solution includes: a REST `Api`, a `PaymentService.Api` responsible for transactions and Outbox writes, a `PaymentService.Worker` for polling Outbox and processing events, and a `PaymentGateway` simulator that receives payment dispatches and emits status messages with realistic delays.

---

### System Architecture
- Services (all run locally on the host machine; not containerized):
    - `Api` (edge): Accepts client `POST /payments`. Forwards to `PaymentService.Api`.
    - `PaymentService.Api`: Transactional write to PostgreSQL `payments` and `outbox` in a single transaction. Exposes an endpoint to confirm dispatch status to support demos and tracing.
    - `PaymentService.Worker`: Outbox poller and dispatcher. Publishes to RabbitMQ using publisher confirms; consumes gateway status updates; applies validation, enrichment, idempotency, and database updates.
    - `PaymentGateway` (simulator): Consumes dispatch messages; emits intermediate and final status messages back to RabbitMQ with realistic delays and occasional faults for retry demonstrations.

- Responsibility Map
    - `Api`: HTTP contract; validation of inbound request shape; calls internal service.
    - `PaymentService.Api`: Strong consistency for `payments` + `outbox`; authoritative state machine; exposes read endpoints.
    - `PaymentService.Worker`: Exactly-once DB side effects using Outbox; at-least-once over Rabbit; idempotent consumer of gateway statuses; backoff and DLX.
    - `PaymentGateway`: Stateless consumer/producer; simulates third-party behavior and intermittent failures.

- Cross-Service Calls (synchronous)
    - `Api` → `PaymentService.Api` (HTTP): create payment.

- Event Flows (asynchronous via RabbitMQ)
    - Outbound: `PaymentService.Worker` → `PaymentGateway` (dispatch command via `x.gateway.request`).
    - Inbound: `PaymentGateway` → `PaymentService.Worker` (status events via `x.gateway.status`).

- Text Diagrams
    - Create:
      Api → HTTP → PaymentService.Api ──(Tx: insert payment + outbox: PaymentCreated)──▶ DB
      PaymentService.Worker ──poll──▶ Outbox ──publish with confirms──▶ x.gateway.request
    - Status updates:
      PaymentGateway ◀─consume── q.gateway.request.<provider>
      PaymentGateway ──publish──▶ x.gateway.status ──▶ q.worker.status (PaymentService.Worker) ──DB update

---

### RabbitMQ Topology
Core principles: topic exchanges, durable queues, persistent messages, per-queue DLX and TTL-based retry queues, publisher confirms, manual acks, idempotent consumers.

Exchanges
- `x.gateway.request` (type: `topic`)
    - Purpose: Commands/dispatches from `PaymentService.Worker` to payment gateways.
    - Routing keys: `gateway.<provider>.payment.created`, `gateway.<provider>.payment.retry`.
- `x.gateway.status` (type: `topic`)
    - Purpose: Status events from gateways back to the worker.
    - Routing keys: `gateway.<provider>.payment.status.intermediate`, `gateway.<provider>.payment.status.final`.
- `x.dlx` (type: `topic`)
    - Purpose: Dead-letter exchange for poison and exhausted retries.
    - Routing keys: `dead.#` (catch-all for DLX paths).

Queues
- `q.gateway.request.simulated`
    - Bindings: `x.gateway.request` with `gateway.simulated.payment.*`.
    - DLX: `x.dlx` with DL routing key `dead.gateway.request.simulated`.
    - Retry queues (TTL + dead-letter back to primary):
        - `q.gateway.request.simulated.retry.5s` (x-message-ttl=5000, DLX: `x.gateway.request`, DL key `gateway.simulated.payment.created`)
        - `q.gateway.request.simulated.retry.30s` (30000 ms)
        - `q.gateway.request.simulated.retry.5m` (300000 ms)
- `q.worker.status`
    - Bindings: `x.gateway.status` with `gateway.*.payment.status.*`.
    - DLX: `x.dlx` with DL routing key `dead.worker.status`.
    - Retry queues:
        - `q.worker.status.retry.5s` (5s → DL back to `x.gateway.status` with original routing key)
        - `q.worker.status.retry.30s`
        - `q.worker.status.retry.5m`
- `q.dlx`
    - Bindings: `x.dlx` with `dead.#`.
    - Purpose: Terminal dead-letter queue for manual inspection.

Consumer responsibilities
- `PaymentGateway` consumes `q.gateway.request.simulated`
    - Manual ack after successful processing.
    - On transient error: reject without requeue → route to appropriate retry queue by republishing (or use consumer-side dead-letter via negative ack with routing to retry exchange; we will implement consumer-driven republish to the retry queue to keep control and headers like `x-retry-count`).
    - On permanent error: route to `x.dlx` (with reason in headers) and ack original to avoid redelivery storm.
- `PaymentService.Worker` consumes `q.worker.status`
    - Idempotent update: check `inbox` table by `message_id` before applying changes.
    - Manual ack only after DB update committed.
    - Retry/backoff on transient failures; DLX on poison.

Retry strategy
- Use 3-tier TTL retry queues (5s, 30s, 5m). Clients increment `x-retry-count` header and choose the next tier. When `x-retry-count` > 3, message is routed to DLX with routing key `dead.<original-key>`.

Publisher confirms & persistence
- All publishes set `deliveryMode=2` (persistent) and require publisher confirms; only mark Outbox record as processed after confirm.

Text Topology Diagram
```
[x.gateway.request] (topic)
  └── (binding: gateway.simulated.payment.*) → [q.gateway.request.simulated]
      ├─ retry: [q.gateway.request.simulated.retry.5s] → DL to x.gateway.request
      ├─ retry: [q.gateway.request.simulated.retry.30s] → DL to x.gateway.request
      └─ retry: [q.gateway.request.simulated.retry.5m] → DL to x.gateway.request

[x.gateway.status] (topic)
  └── (binding: gateway.*.payment.status.*) → [q.worker.status]
      ├─ retry: [q.worker.status.retry.5s] → DL to x.gateway.status
      ├─ retry: [q.worker.status.retry.30s] → DL to x.gateway.status
      └─ retry: [q.worker.status.retry.5m] → DL to x.gateway.status

[x.dlx] (topic)
  └── [q.dlx]
```

---

### Database Schema (PostgreSQL)
- `payments`
    - `payment_id` UUID PK
    - `amount` numeric(18,2) NOT NULL
    - `currency` char(3) NOT NULL
    - `customer_id` UUID NOT NULL
    - `status` varchar(32) NOT NULL (values: `Created`, `Validated`, `Dispatched`, `Authorized`, `Captured`, `Failed`, `Cancelled`)
    - `gateway_provider` varchar(64) NOT NULL DEFAULT 'simulated'
    - `external_reference` varchar(128) NULL
    - `created_at` timestamptz NOT NULL DEFAULT now()
    - `updated_at` timestamptz NOT NULL DEFAULT now()
    - `version` int NOT NULL DEFAULT 0 (optimistic concurrency)
    - Indexes: `idx_payments_status`, `idx_payments_customer_id`, `idx_payments_created_at`

- `payment_status_history` (recommended)
    - `id` bigserial PK
    - `payment_id` UUID NOT NULL REFERENCES `payments` ON DELETE CASCADE
    - `status` varchar(32) NOT NULL
    - `reason` varchar(256) NULL
    - `metadata` jsonb NULL
    - `source` varchar(64) NOT NULL (e.g., `Worker`, `Gateway`)
    - `occurred_at` timestamptz NOT NULL DEFAULT now()
    - Index: (`payment_id`, `occurred_at`)

- `outbox`
    - `id` bigserial PK
    - `aggregate_type` varchar(64) NOT NULL (e.g., `Payment`)
    - `aggregate_id` UUID NOT NULL (== `payment_id`)
    - `type` varchar(128) NOT NULL (message type name)
    - `payload` jsonb NOT NULL
    - `occurred_at` timestamptz NOT NULL DEFAULT now()
    - `processing_status` varchar(16) NOT NULL DEFAULT 'Pending' (enum: Pending, Processing, Sent, Failed)
    - `attempt_count` int NOT NULL DEFAULT 0
    - `locked_until` timestamptz NULL
    - `lock_token` uuid NULL
    - `processed_at` timestamptz NULL
    - `last_error` text NULL
    - Indexes: `idx_outbox_status_occurred` on (`processing_status`, `occurred_at`), `idx_outbox_locked_until` on (`locked_until`), `idx_outbox_aggregate` on (`aggregate_type`, `aggregate_id`)

- `inbox`
    - `message_id` uuid PK
    - `source` varchar(64) NOT NULL (e.g., `gateway.simulated`)
    - `received_at` timestamptz NOT NULL DEFAULT now()
    - `payload_hash` char(64) NULL (optional integrity check)
    - Index: (`source`, `message_id`)

Rationale
- `payments` holds current state; `payment_status_history` preserves a full audit trail.
- `outbox` guarantees atomic write of intent to publish within the same transaction as domain changes.
- `inbox` ensures idempotent consumer semantics for gateway status updates.

---

### Message Contracts
All messages include these common headers/properties: `message_id` (UUID), `correlation_id` (UUID, equals `payment_id`), `causation_id` (ancestor message id), `occurred_at` (UTC), `trace_id` (for distributed tracing), `retry_count` (int), `provider` (e.g., `simulated`), and `schema_version` (int starting at 1). Bodies are JSON.

- `PaymentCreated` (event → Outbox)
    - Body fields:
        - `payment_id` UUID
        - `amount` decimal
        - `currency` string(3)
        - `customer_id` UUID
        - `gateway_provider` string
        - `idempotency_key` string (from API)

- `PaymentValidated` (event → Outbox, after worker validation)
    - Body: `payment_id`, `validation_warnings` [string], `enrichment` object (e.g., risk score), `validated_at` timestamp.

- `PaymentDispatchedToGateway` (event → status/observability; optional to expose)
    - Body: `payment_id`, `provider`, `dispatched_at`.

- `GatewayIntermediateStatus` (event from gateway)
    - Body: `payment_id`, `provider`, `status_code` string (e.g., `AuthorizedPendingCapture`, `Pending3DS`), `details` object, `emitted_at` timestamp.

- `GatewayFinalStatus` (event from gateway)
    - Body: `payment_id`, `provider`, `final_status` string from {`Authorized`, `Captured`, `Failed`, `Cancelled`}, `reason` string?, `emitted_at` timestamp.

- `PaymentError` (event)
    - Body: `payment_id`, `stage` string, `error_code`, `error_message`, `emitted_at`.

Reasoning
- Distinguish intermediate vs final enables accurate state transitions and observability.
- Include `schema_version` for forward compatibility.

---

### Service Interaction Flows
1) Create Payment (synchronous + outbox)
- `Api` receives `POST /payments` with `amount`, `currency`, `customer_id`, `provider`, `idempotency_key`.
- `Api` → `PaymentService.Api`: HTTP call.
- `PaymentService.Api` transaction:
    - Insert row in `payments` (`status=Created`).
    - Insert `outbox` record with `PaymentCreated` payload referencing `payment_id`.
    - Commit.
- Return 202/201 with `payment_id` to client.

2) Dispatch to Gateway (async)
- `PaymentService.Worker` polls `outbox` (see Outbox Workflow) for `PaymentCreated`.
- Validates/enriches; writes `PaymentValidated` outbox event (optional) within a DB transaction updating `payments.status=Validated`.
- Publishes dispatch command to `x.gateway.request` with routing key `gateway.simulated.payment.created` and persistent message; on confirm, marks original Outbox item as `Sent`; updates `payments.status=Dispatched`.

3) Gateway Processing → Status Updates
- `PaymentGateway` consumes `q.gateway.request.simulated`, introduces delay(s), emits `GatewayIntermediateStatus` and then `GatewayFinalStatus` to `x.gateway.status` with appropriate keys.
- `PaymentService.Worker` consumes `q.worker.status`, ensures idempotency (check `inbox` by `message_id`), updates `payments` to the new state and appends `payment_status_history` row, commits, then ack.

4) Errors & Retries
- For transient errors on either consumer, republish to the next retry queue tier with incremented `x-retry-count`.
- After 3 attempts, route to `x.dlx` → `q.dlx` for manual inspection; emit `PaymentError` for observability.

---

### Outbox Workflow
- Insert phase (within `PaymentService.Api`):
    - Single transaction: insert into `payments`; insert `outbox` row with `processing_status='Pending'` and the `PaymentCreated` payload; commit.

- Polling & locking (within `PaymentService.Worker`):
    - Query in batches of N (e.g., 50):
      ```sql
      update outbox o
      set processing_status = 'Processing',
          locked_until = now() + interval '30 seconds',
          lock_token = gen_random_uuid(),
          attempt_count = attempt_count + 1
      where id in (
        select id from outbox
        where processing_status = 'Pending'
          and (locked_until is null or locked_until < now())
        order by occurred_at
        for update skip locked
        limit 50
      )
      returning *;
      ```
    - The worker uses the returned `lock_token` to ensure only the locker can mark completion.

- Publish & confirm
    - Publish to `x.gateway.request` with persistent mode and mandatory flag; await confirms.
    - On confirm: in a new transaction, mark `outbox.processed_at=now()`, `processing_status='Sent'`, clear lock; update `payments` to `Dispatched`; append `payment_status_history`.
    - On NACK/timeouts: keep item as `Pending` or `Failed` with backoff by extending `locked_until` based on `attempt_count` (e.g., 5s, 30s, 5m) so another poll cycle can retry.

- Idempotency
    - Publishing: Outbox guarantees at-least-once publish; gateway must handle duplicates. Since our `PaymentGateway` simulator is idempotent (dedupe by `message_id`), no harm on duplicate publishes.
    - Consuming statuses: `inbox` deduplication by `message_id` ensures idempotent updates. Use unique constraint on `message_id` and only apply state change if an insert into `inbox` succeeds.

- Failure recovery
    - If the worker crashes after publish but before marking `Sent`, duplicate publishes are possible; gateway and `inbox` dedupe prevent double-processing.
    - If the worker crashes while `Processing`, the `locked_until` timeout releases the record for retry by any worker instance.

---

### Development Plan (Detailed Task List)
Order: infra → topology → schema → .NET services → testing → docs.

1) Infrastructure (Docker Compose) ✓
- Create `docker/docker-compose.yml` with services:
    - `rabbitmq`: `rabbitmq:3.13-management`, ports 5672, 15672, volumes mounting `rabbitmq/definitions.json` and `rabbitmq/enabled_plugins` if needed; load definitions via `RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS` or `RABBITMQ_LOAD_DEFINITIONS` env.
    - `postgres`: `postgres:16`, port 5432, env for `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`, and mount `postgres/initdb.d/*.sql` for schema.
- Network: default bridge. No app services.
- Local connection strings: Rabbit `amqp://guest:guest@localhost:5672/`; Postgres `Host=localhost;Port=5432;Database=payments;Username=app;Password=app;Pooling=true`.

2) RabbitMQ Topology Implementation ✓
- Author `docker/rabbitmq/definitions.json` containing exchanges, queues, bindings, DLX and policies (e.g., `ha-mode: all` optional for demo, and `queue-master-locator: client-local` not necessary locally).
- Define policies for dead-lettering and message TTLs on retry queues.

3) DB Schema & Migrations ✓
- Create `docker/postgres/initdb.d/0001_schema.sql` with the four tables and indexes.
- Optionally add `0002_seed.sql` for demo data.

4) .NET Implementation
- `src/Api` (minimal API) ✓
    - POST `/payments`: validate request, forward to `PaymentService.Api` via HttpClient.
    - Provide 201 + `payment_id`.
- `src/PaymentService.Api` ✓
    - EF Core or Dapper for DB. Implement transactional handler:
        - Begin tx → insert `payments` → insert `outbox`(`PaymentCreated`) → commit.
    - GET `/payments/{id}` returns current state.
    - POST `/outbox/{id}/confirm` (optional demo endpoint) to view/inspect outbox item.
- `src/PaymentService.Worker` ✓
    - HostedService: polling loop with cancellation, jitter, and max batch size.
    - Outbox claim (locking SQL), publish to Rabbit with confirmations.
    - Update `payments` to `Validated`/`Dispatched`. Append to `payment_status_history`.
    - Consumer for `q.worker.status` with manual acks; insert into `inbox` then update `payments` and history in same transaction; ack on success.
    - Retry to retry-queues on transient errors; route to DLX after 3 tries.
- `src/PaymentGateway` ✓
    - Consumer for `q.gateway.request.simulated`; simulate delay (Task.Delay random jitter) and produce 1–2 intermediate events and then final.
    - Ensure idempotency by tracking recent `message_id` in-memory cache (LRU) or by dedupe header.

5) Error Handling & Retry ✓
- Common library for RabbitMQ producers/consumers: publisher confirms, mandatory publishes, topology names, headers (`x-retry-count`), and a retry policy helper.
- Map exceptions into retryable vs terminal. Include reason headers when sending to DLX.

6) Testing Scenarios ✓
- Unit tests:
    - Outbox locking and state transitions (using in-memory or local test DB).
    - Idempotent consumer logic (`inbox` uniqueness).
- Integration tests (require Docker infra running):
    - End-to-end create → dispatch → status updates.
    - Retry ladder: force gateway to throw transient errors N times → observe backoff.
    - Poison: force permanent error → assert DLX route and `q.dlx` message.

7) Observability ✓
- Logging: Serilog (to console) with correlation/trace ids.
- Tracing: OpenTelemetry instrumentation for HTTP and RabbitMQ publish/consume spans.
- Metrics: counters for published/consumed, retries, DLX; optional Prometheus exporter.

8) Documentation ✓
- `/docs/architecture.md` with diagrams and flows.
- `/docs/message_contracts.md` with JSON schemas and evolution guidance.
- `README.md` quickstart.

---

### Best Practices Checklist
- .NET 10 worker service design
    - Use `BackgroundService` with cooperative cancellation ✓
    - Bounded concurrency for polling and publishes ✓
    - Jitter on polling/backoff to avoid thundering herd ✓
    - Respect `IHostApplicationLifetime` for graceful shutdown ✓

- Clean architecture principles
    - Separate application, infrastructure, and presentation layers ✓
    - Domain models decoupled from transport (Rabbit/HTTP) ✓
    - Configuration via `IOptions` with clear defaults ✓

- RabbitMQ resiliency
    - Durable queues, persistent messages, publisher confirms ✓
    - Manual acks, consumer prefetch (e.g., `prefetch=32`) ✓
    - Retry with TTL queues and DLX for poison ✓
    - Idempotent consumers with `inbox` ✓

- Outbox correctness
    - Insert domain change + outbox record in single DB transaction ✓
    - Locking with `skip locked` and `locked_until` timeout ✓
    - Mark processed only after publisher confirm ✓
    - Handle publisher NACK/timeouts with backoff ✓

- Idempotent consumers
    - Deduplicate by `message_id` using `inbox` unique PK ✓
    - Apply state transitions only after a successful `inbox` insert ✓

- Transaction safety
    - Use READ COMMITTED or REPEATABLE READ isolation; avoid phantom via `for update skip locked` ✓
    - Optimistic concurrency via `version` column when editing `payments` ✓

- Local dev workflow
    - Docker Compose up infra → run .NET services locally with `ASPNETCORE_ENVIRONMENT=Development` ✓
    - Connection strings target `localhost` mapped ports ✓

- Message versioning
    - `schema_version` field; tolerant readers; additive change policy ✓

- Error handling & observability
    - Structured logs with message ids and routing keys ✓
    - Spans for publish/consume and DB operations ✓
    - Metrics for retries and DLX counts ✓

---

### Repository Structure
```
/ (repo root)
  /src
    /Api
    /PaymentService.Api
    /PaymentService.Worker
    /PaymentGateway
    /Shared (optional common abstractions: messaging, contracts, options)
  /docker
    docker-compose.yml
    /rabbitmq
      definitions.json
      enabled_plugins (optional)
    /postgres
      initdb.d
        0001_schema.sql
  /tests
    /PaymentService.Worker.Tests
    /PaymentService.Api.Tests (optional)
  /docs
    architecture.md
    message_contracts.md
  README.md
  LICENSE
```

---

### Local Development Environment (Docker Compose) – concrete plan
- `docker-compose.yml` sketch:
  ```yaml
  version: '3.9'
  services:
    rabbitmq:
      image: rabbitmq:3.13-management
      ports: ["5672:5672", "15672:15672"]
      environment:
        RABBITMQ_DEFAULT_USER: guest
        RABBITMQ_DEFAULT_PASS: guest
        RABBITMQ_LOAD_DEFINITIONS: "true"
      volumes:
        - ./rabbitmq/definitions.json:/etc/rabbitmq/definitions.json:ro
      networks: [default]

    postgres:
      image: postgres:16
      ports: ["5432:5432"]
      environment:
        POSTGRES_USER: app
        POSTGRES_PASSWORD: app
        POSTGRES_DB: payments
      volumes:
        - ./postgres/initdb.d:/docker-entrypoint-initdb.d:ro
      networks: [default]
  ```
- .NET local configs:
    - RabbitMQ: `amqp://guest:guest@localhost:5672/`
    - PostgreSQL: `Host=localhost;Port=5432;Database=payments;Username=app;Password=app;Include Error Detail=true;Maximum Pool Size=50`
- Developer workflow:
    1) `docker compose -f docker/docker-compose.yml up -d`
    2) Start `PaymentService.Api`, then `PaymentService.Worker`, then `PaymentGateway`, finally `Api`.
    3) POST to `Api /payments` with payload; observe logs, RabbitMQ management UI, and DB state.

If you want, I can generate concrete `definitions.json`, `0001_schema.sql`, and configuration code snippets next.

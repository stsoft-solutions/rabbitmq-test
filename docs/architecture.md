### Architecture Overview

This document summarizes the local-first architecture for the payments demo:

- All .NET apps run locally (Api, PaymentService.Api, PaymentService.Worker, PaymentGateway).
- Docker hosts only RabbitMQ and PostgreSQL.
- Outbox pattern ensures DB changes and intent-to-publish are persisted atomically.
- PaymentService.Worker polls Outbox, publishes with RabbitMQ publisher confirms, and consumes gateway status updates idempotently.
- RabbitMQ uses topic exchanges, durable queues, TTL-based retry tiers, and a DLX for poison/exhausted messages.

See the repository README for complete details and the RabbitMQ definitions for the full topology.

# RabbitMQ Test System

## Prerequisites
- .NET 10 SDK
- Docker Desktop

## Running Infrastructure
```powershell
cd docker
docker compose up -d
```

## Building & Testing
```powershell
dotnet build rabbitmq-poc.slnx
dotnet test rabbitmq-poc.slnx
```

## Services
- `src/Api` – REST facade with idempotency support.
- `src/PaymentService.Api` – Payment persistence + outbox writer.
- `src/PaymentService.Worker` – Outbox dispatcher + gateway consumer.
- `src/PaymentGateway` – Mock payment processor.

## Documentation
See `docs/architecture.md` and `docs/message_contracts.md` for deeper details.


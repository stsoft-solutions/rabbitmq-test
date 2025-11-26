# Message Contracts

## PaymentCreated
- `messageId: uuid` – unique identifier, set as RabbitMQ `message_id`.
- `paymentId: uuid`
- `clientRequestId: uuid`
- `amountMinor: long`
- `currencyCode: string(3)`
- `gatewayCode: string(32)`
- `correlationId: uuid`
- `causationId: uuid`
- `occurredUtc: DateTime`

## PaymentValidated
- `messageId: uuid`
- `paymentId: uuid`
- `validationResult: string`
- `reasons: string[]`
- `correlationId: uuid`
- `causationId: uuid`
- `occurredUtc: DateTime`

## PaymentDispatchedToGateway
- `messageId: uuid`
- `paymentId: uuid`
- `gatewayCode: string`
- `dispatchAttempt: int`
- `payloadChecksum: string`
- `correlationId: uuid`
- `causationId: uuid`
- `occurredUtc: DateTime`

## GatewayIntermediateStatus
- `messageId: uuid`
- `paymentId: uuid`
- `gatewayCode: string`
- `status: string (Pending|3DS|ManualReview)`
- `details: object`
- `correlationId: uuid`
- `causationId: uuid`
- `occurredUtc: DateTime`

## GatewayFinalStatus
- `messageId: uuid`
- `paymentId: uuid`
- `gatewayCode: string`
- `status: string (Succeeded|Failed|Cancelled)`
- `failureReason: string`
- `authorizationCode: string`
- `correlationId: uuid`
- `causationId: uuid`
- `occurredUtc: DateTime`

## PaymentError
- `messageId: uuid`
- `paymentId: uuid`
- `stage: string (Outbox|Gateway|Consumer)`
- `severity: string`
- `details: object`
- `correlationId: uuid`
- `causationId: uuid`
- `occurredUtc: DateTime`


DROP SCHEMA IF EXISTS payments CASCADE;
CREATE SCHEMA payments;

CREATE TYPE payments.payment_status AS ENUM (
    'Pending',
    'Validated',
    'Dispatched',
    'PendingGateway',
    'Processing',
    'Succeeded',
    'Failed'
);

CREATE TABLE payments.payments (
    payment_id UUID PRIMARY KEY,
    client_request_id UUID UNIQUE NOT NULL,
    amount_minor BIGINT NOT NULL CHECK (amount_minor > 0),
    currency_code CHAR(3) NOT NULL,
    customer_id UUID NOT NULL,
    gateway_code VARCHAR(32) NOT NULL,
    status payments.payment_status NOT NULL DEFAULT 'Pending',
    created_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    version BIGINT NOT NULL DEFAULT 1
);

CREATE TABLE payments.payment_status_history (
    history_id BIGSERIAL PRIMARY KEY,
    payment_id UUID NOT NULL REFERENCES payments.payments(payment_id),
    previous_status payments.payment_status,
    new_status payments.payment_status NOT NULL,
    reason_code VARCHAR(64),
    payload JSONB,
    occurred_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    correlation_id UUID NOT NULL
);

CREATE TABLE payments.outbox (
    outbox_id BIGSERIAL PRIMARY KEY,
    payment_id UUID NOT NULL REFERENCES payments.payments(payment_id),
    event_type VARCHAR(64) NOT NULL,
    payload JSONB NOT NULL,
    routing_key VARCHAR(128) NOT NULL,
    created_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    processed_utc TIMESTAMPTZ,
    attempts INT NOT NULL DEFAULT 0,
    trace_id UUID NOT NULL
);

CREATE TABLE payments.inbox (
    inbox_id BIGSERIAL PRIMARY KEY,
    message_id UUID NOT NULL UNIQUE,
    payment_id UUID NOT NULL REFERENCES payments.payments(payment_id),
    source_exchange VARCHAR(64) NOT NULL,
    payload_hash CHAR(64) NOT NULL,
    processed_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

INSERT INTO payments.payments (payment_id, client_request_id, amount_minor, currency_code, customer_id, gateway_code)
VALUES
    ('00000000-0000-0000-0000-000000000001', '11111111-1111-1111-1111-111111111111', 1000, 'USD', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'mockpay');

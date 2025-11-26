-- Initialize payments database schema
-- This script is executed by the official Postgres image on first container start.

-- Enable useful extensions
CREATE EXTENSION IF NOT EXISTS pgcrypto; -- for gen_random_uuid()

-- Ensure app role exists (created by env), grant privileges
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT FROM pg_roles WHERE rolname = 'app'
  ) THEN
    CREATE ROLE app LOGIN PASSWORD 'app';
  END IF;
END$$;

-- Schema (use public for simplicity)
SET ROLE postgres;

-- payments table
CREATE TABLE IF NOT EXISTS payments (
  payment_id UUID PRIMARY KEY,
  amount NUMERIC(18,2) NOT NULL,
  currency CHAR(3) NOT NULL,
  customer_id UUID NOT NULL,
  status VARCHAR(32) NOT NULL,
  gateway_provider VARCHAR(64) NOT NULL DEFAULT 'simulated',
  external_reference VARCHAR(128) NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  version INT NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_payments_status ON payments(status);
CREATE INDEX IF NOT EXISTS idx_payments_customer_id ON payments(customer_id);
CREATE INDEX IF NOT EXISTS idx_payments_created_at ON payments(created_at);

-- Trigger to maintain updated_at
CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
  NEW.updated_at = now();
  RETURN NEW;
END; $$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_set_updated_at ON payments;
CREATE TRIGGER trg_set_updated_at
BEFORE UPDATE ON payments
FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- payment_status_history table
CREATE TABLE IF NOT EXISTS payment_status_history (
  id BIGSERIAL PRIMARY KEY,
  payment_id UUID NOT NULL REFERENCES payments(payment_id) ON DELETE CASCADE,
  status VARCHAR(32) NOT NULL,
  reason VARCHAR(256) NULL,
  metadata JSONB NULL,
  source VARCHAR(64) NOT NULL,
  occurred_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_psh_payment_occurred ON payment_status_history(payment_id, occurred_at);

-- outbox table
CREATE TABLE IF NOT EXISTS outbox (
  id BIGSERIAL PRIMARY KEY,
  aggregate_type VARCHAR(64) NOT NULL,
  aggregate_id UUID NOT NULL,
  type VARCHAR(128) NOT NULL,
  payload JSONB NOT NULL,
  -- OpenTelemetry context to preserve trace across outbox boundary
  message_id UUID NOT NULL DEFAULT gen_random_uuid(),
  traceparent TEXT NULL,
  tracestate TEXT NULL,
  baggage TEXT NULL,
  occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  processing_status VARCHAR(16) NOT NULL DEFAULT 'Pending',
  attempt_count INT NOT NULL DEFAULT 0,
  locked_until TIMESTAMPTZ NULL,
  lock_token UUID NULL,
  processed_at TIMESTAMPTZ NULL,
  last_error TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_outbox_status_occurred ON outbox(processing_status, occurred_at);
CREATE INDEX IF NOT EXISTS idx_outbox_locked_until ON outbox(locked_until);
CREATE INDEX IF NOT EXISTS idx_outbox_aggregate ON outbox(aggregate_type, aggregate_id);

-- inbox table
CREATE TABLE IF NOT EXISTS inbox (
  message_id UUID PRIMARY KEY,
  source VARCHAR(64) NOT NULL,
  received_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  payload_hash CHAR(64) NULL
);

CREATE INDEX IF NOT EXISTS idx_inbox_source_message ON inbox(source, message_id);

-- Permissions
ALTER TABLE payments OWNER TO app;
ALTER TABLE payment_status_history OWNER TO app;
ALTER TABLE outbox OWNER TO app;
ALTER TABLE inbox OWNER TO app;

GRANT SELECT, INSERT, UPDATE, DELETE ON payments TO app;
GRANT SELECT, INSERT ON payment_status_history TO app;
GRANT SELECT, INSERT, UPDATE ON outbox TO app;
GRANT SELECT, INSERT ON inbox TO app;

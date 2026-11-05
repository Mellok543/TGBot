-- Reklio payment storage, migration 001.
-- Run manually after taking a database backup. This migration does not enable payments.
CREATE TABLE IF NOT EXISTS reklio_payment_attempts (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    order_id BIGINT NOT NULL,
    advertiser_id BIGINT NOT NULL,
    amount_rub DECIMAL(14,2) NOT NULL,
    currency CHAR(3) NOT NULL DEFAULT 'RUB',
    provider VARCHAR(32) NOT NULL DEFAULT 'yookassa',
    mode VARCHAR(16) NOT NULL DEFAULT 'test',
    status VARCHAR(32) NOT NULL DEFAULT 'Created',
    idempotence_key CHAR(36) NOT NULL,
    provider_payment_id VARCHAR(100) NULL,
    confirmation_url TEXT NULL,
    last_error VARCHAR(500) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    CONSTRAINT chk_reklio_payment_positive CHECK (amount_rub > 0),
    UNIQUE KEY uq_reklio_payment_idempotence (idempotence_key),
    UNIQUE KEY uq_reklio_payment_provider_id (provider_payment_id),
    KEY idx_reklio_payment_order (order_id),
    KEY idx_reklio_payment_status (status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS reklio_payment_events (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    payment_attempt_id BIGINT NOT NULL,
    provider_status VARCHAR(32) NOT NULL,
    event_type VARCHAR(64) NOT NULL,
    occurred_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT fk_reklio_payment_events_attempt
      FOREIGN KEY (payment_attempt_id) REFERENCES reklio_payment_attempts(id),
    KEY idx_reklio_payment_events_attempt (payment_attempt_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

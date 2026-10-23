-- Apply manually before enabling REKLIO_PUBLISH_ENABLED.
CREATE TABLE IF NOT EXISTS reklio_publications (
  order_id BIGINT NOT NULL PRIMARY KEY,
  channel_id BIGINT NOT NULL,
  telegram_message_id BIGINT NULL,
  state VARCHAR(32) NOT NULL DEFAULT 'Claimed',
  claimed_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  published_at DATETIME(6) NULL,
  delete_after_hours INT NULL,
  deleted_at DATETIME(6) NULL,
  last_error VARCHAR(500) NULL,
  KEY idx_reklio_publications_state (state,claimed_at),
  KEY idx_reklio_publications_expiry (published_at,deleted_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Apply with backup before enabling MTProto monitoring; no changes to payment provider tables.
CREATE TABLE IF NOT EXISTS reklio_post_monitor (
  order_id BIGINT NOT NULL PRIMARY KEY,
  last_result VARCHAR(16) NOT NULL DEFAULT 'UNKNOWN',
  checked_at DATETIME NOT NULL,
  missing_count INT NOT NULL DEFAULT 0,
  first_missing_at DATETIME NULL,
  CONSTRAINT fk_reklio_post_monitor_order FOREIGN KEY (order_id) REFERENCES ad_requests(id),
  INDEX idx_monitor_checked (checked_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

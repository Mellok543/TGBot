-- Add delivery tracking to existing notification_outbox.
-- First inspect table schema before applying; this migration assumes the table has an id PK.
ALTER TABLE notification_outbox
  ADD COLUMN IF NOT EXISTS delivery_state VARCHAR(20) NOT NULL DEFAULT 'Pending',
  ADD COLUMN IF NOT EXISTS delivery_attempts INT NOT NULL DEFAULT 0,
  ADD COLUMN IF NOT EXISTS delivery_next_at DATETIME(6) NULL,
  ADD COLUMN IF NOT EXISTS delivery_claimed_at DATETIME(6) NULL,
  ADD COLUMN IF NOT EXISTS delivery_sent_at DATETIME(6) NULL,
  ADD COLUMN IF NOT EXISTS delivery_last_error VARCHAR(200) NULL;

CREATE INDEX IF NOT EXISTS idx_reklio_notification_delivery
  ON notification_outbox(delivery_state,delivery_next_at);

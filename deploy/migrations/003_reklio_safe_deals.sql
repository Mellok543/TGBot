-- Safe Deal preparation; does not activate payments or payouts.
CREATE TABLE IF NOT EXISTS reklio_safe_deals (
  order_id BIGINT NOT NULL PRIMARY KEY,
  owner_id BIGINT NOT NULL,
  provider_deal_id VARCHAR(100) NULL,
  create_key CHAR(36) NOT NULL,
  status VARCHAR(32) NOT NULL DEFAULT 'Created',
  commission_percent DECIMAL(5,2) NOT NULL,
  gross_rub DECIMAL(14,2) NOT NULL,
  commission_rub DECIMAL(14,2) NOT NULL,
  seller_rub DECIMAL(14,2) NOT NULL,
  expires_at DATETIME NULL,
  payout_status VARCHAR(32) NOT NULL DEFAULT 'NotRequested',
  provider_payout_id VARCHAR(100) NULL,
  payout_key CHAR(36) NULL,
  created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  UNIQUE KEY uq_reklio_safe_deal_provider (provider_deal_id),
  UNIQUE KEY uq_reklio_safe_deal_create (create_key),
  UNIQUE KEY uq_reklio_safe_deal_payout (provider_payout_id),
  CONSTRAINT chk_reklio_safe_deal_amounts CHECK (gross_rub=commission_rub+seller_rub AND seller_rub>0 AND commission_rub>=0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS reklio_seller_payout_profiles (
  owner_id BIGINT NOT NULL PRIMARY KEY,
  payout_method VARCHAR(20) NOT NULL DEFAULT 'unconfigured',
  provider_payout_token VARCHAR(512) NULL,
  updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

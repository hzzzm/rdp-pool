-- Queue heartbeat + durable end times for 30-day wait stats.
USE rdp_pool;

ALTER TABLE queue_entries
  ADD COLUMN heartbeat_at DATETIME(3) NULL AFTER updated_at,
  ADD COLUMN ended_at DATETIME(3) NULL AFTER heartbeat_at,
  ADD KEY idx_queue_heartbeat (status, heartbeat_at),
  ADD KEY idx_queue_ended (group_id, ended_at, status);

UPDATE queue_entries
SET heartbeat_at = COALESCE(updated_at, created_at)
WHERE status IN ('waiting', 'offered') AND heartbeat_at IS NULL;

CREATE TABLE IF NOT EXISTS queue_history (
  id              VARCHAR(32)  NOT NULL PRIMARY KEY,
  queue_entry_id  VARCHAR(32)  NOT NULL,
  group_id        VARCHAR(32)  NOT NULL,
  user_id         VARCHAR(32)  NOT NULL,
  started_at      DATETIME(3)  NOT NULL,
  ended_at        DATETIME(3)  NOT NULL,
  wait_sec        INT          NOT NULL,
  outcome         VARCHAR(32)  NOT NULL,
  KEY idx_qh_group_ended (group_id, ended_at),
  KEY idx_qh_ended (ended_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT INTO pool_settings (`key`, value, updated_at) VALUES
  ('queue_heartbeat_ttl_sec', '45', NOW(3))
ON DUPLICATE KEY UPDATE value = VALUES(value), updated_at = VALUES(updated_at);
-- Applied on first MySQL container boot. Keep in sync with server/migrations/001_init.sql

USE rdp_pool;

CREATE TABLE IF NOT EXISTS resource_groups (
  id          VARCHAR(32)  NOT NULL PRIMARY KEY,
  code        VARCHAR(64)  NOT NULL,
  name        VARCHAR(128) NOT NULL,
  enabled     TINYINT(1)   NOT NULL DEFAULT 1,
  sort_order  INT          NOT NULL DEFAULT 0,
  description VARCHAR(512) NULL,
  created_at  DATETIME(3)  NOT NULL,
  updated_at  DATETIME(3)  NOT NULL,
  UNIQUE KEY uk_resource_groups_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS pool_users (
  id            VARCHAR(32)  NOT NULL PRIMARY KEY,
  login         VARCHAR(64)  NOT NULL,
  password_hash VARCHAR(255) NOT NULL,
  display_name  VARCHAR(128) NULL,
  enabled       TINYINT(1)   NOT NULL DEFAULT 1,
  created_at    DATETIME(3)  NOT NULL,
  updated_at    DATETIME(3)  NOT NULL,
  UNIQUE KEY uk_pool_users_login (login)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS user_group_grants (
  user_id    VARCHAR(32) NOT NULL,
  group_id   VARCHAR(32) NOT NULL,
  created_at DATETIME(3) NOT NULL,
  PRIMARY KEY (user_id, group_id),
  KEY idx_grants_group (group_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS user_group_affinities (
  user_id          VARCHAR(32) NOT NULL,
  group_id         VARCHAR(32) NOT NULL,
  last_resource_id VARCHAR(32) NOT NULL,
  updated_at       DATETIME(3) NOT NULL,
  PRIMARY KEY (user_id, group_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS resources (
  id                  VARCHAR(32)  NOT NULL PRIMARY KEY,
  group_id            VARCHAR(32)  NOT NULL,
  name                VARCHAR(128) NOT NULL,
  host                VARCHAR(255) NOT NULL,
  rdp_port            INT          NOT NULL DEFAULT 3389,
  status              VARCHAR(32)  NOT NULL,
  tags                JSON         NULL,
  current_session_id  VARCHAR(32)  NULL,
  supply_class        VARCHAR(16)  NULL,
  supply_seq          BIGINT       NULL,
  linger_hint         TINYINT(1)   NOT NULL DEFAULT 0,
  last_soft_release_at DATETIME(3) NULL,
  enabled             TINYINT(1)   NOT NULL DEFAULT 1,
  created_at          DATETIME(3)  NOT NULL,
  updated_at          DATETIME(3)  NOT NULL,
  KEY idx_resources_group_status (group_id, status),
  KEY idx_resources_supply (group_id, status, supply_class, supply_seq)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS resource_secrets (
  resource_id VARCHAR(32)   NOT NULL PRIMARY KEY,
  ciphertext  VARBINARY(2048) NOT NULL,
  key_version INT           NOT NULL DEFAULT 1,
  updated_at  DATETIME(3)   NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS sessions (
  id                      VARCHAR(32)  NOT NULL PRIMARY KEY,
  user_id                 VARCHAR(32)  NOT NULL,
  group_id                VARCHAR(32)  NOT NULL,
  resource_id             VARCHAR(32)  NOT NULL,
  state                   VARCHAR(32)  NOT NULL,
  close_reason            VARCHAR(64)  NULL,
  lease_expires_at        DATETIME(3)  NULL,
  occupation_deadline_at  DATETIME(3)  NULL,
  client_instance_id      VARCHAR(64)  NULL,
  rdp_connected           TINYINT(1)   NOT NULL DEFAULT 0,
  meta_json               JSON         NULL,
  created_at              DATETIME(3)  NOT NULL,
  closed_at               DATETIME(3)  NULL,
  active_user_key VARCHAR(32)
    GENERATED ALWAYS AS (IF(state IN ('pending','active'), user_id, NULL)) STORED,
  active_resource_key VARCHAR(32)
    GENERATED ALWAYS AS (IF(state IN ('pending','active'), resource_id, NULL)) STORED,
  UNIQUE KEY uk_active_user (active_user_key),
  UNIQUE KEY uk_active_resource (active_resource_key),
  KEY idx_sessions_user_state (user_id, state),
  KEY idx_sessions_group_state (group_id, state),
  KEY idx_sessions_resource (resource_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS queue_entries (
  id                   VARCHAR(32) NOT NULL PRIMARY KEY,
  group_id             VARCHAR(32) NOT NULL,
  user_id              VARCHAR(32) NOT NULL,
  status               VARCHAR(32) NOT NULL,
  seq                  BIGINT      NOT NULL,
  offered_resource_id  VARCHAR(32) NULL,
  offered_until        DATETIME(3) NULL,
  created_at           DATETIME(3) NOT NULL,
  updated_at           DATETIME(3) NOT NULL,
  KEY idx_queue_group_status_seq (group_id, status, seq),
  KEY idx_queue_user_status (user_id, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS pool_settings (
  `key`      VARCHAR(64) NOT NULL PRIMARY KEY,
  value      TEXT        NOT NULL,
  updated_at DATETIME(3) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS audit_logs (
  id          BIGINT       NOT NULL AUTO_INCREMENT PRIMARY KEY,
  at          DATETIME(3)  NOT NULL,
  actor_type  VARCHAR(32)  NOT NULL,
  actor_id    VARCHAR(64)  NOT NULL,
  action      VARCHAR(64)  NOT NULL,
  target_type VARCHAR(32)  NULL,
  target_id   VARCHAR(64)  NULL,
  meta_json   JSON         NULL,
  ip          VARCHAR(64)  NULL,
  KEY idx_audit_at (at),
  KEY idx_audit_action (action)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS rdp_tickets (
  id         VARCHAR(32) NOT NULL PRIMARY KEY,
  session_id VARCHAR(32) NOT NULL,
  expires_at DATETIME(3) NOT NULL,
  used_at    DATETIME(3) NULL,
  created_at DATETIME(3) NOT NULL,
  KEY idx_tickets_session (session_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT INTO pool_settings (`key`, value, updated_at) VALUES
  ('pending_confirm_sec', '90', NOW(3)),
  ('heartbeat_interval_sec', '15', NOW(3)),
  ('lease_ttl_sec', '60', NOW(3)),
  ('max_occupation_ttl_sec', '14400', NOW(3)),
  ('offer_ttl_sec', '60', NOW(3)),
  ('ticket_ttl_sec', '120', NOW(3)),
  ('linger_hint_ttl_sec', '1800', NOW(3)),
  ('linger_offer_scan_sec', '20', NOW(3))
ON DUPLICATE KEY UPDATE value = VALUES(value), updated_at = VALUES(updated_at);

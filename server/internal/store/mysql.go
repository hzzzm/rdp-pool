package store

import (
	"context"
	"database/sql"
	"fmt"
	"time"

	_ "github.com/go-sql-driver/mysql"
	"github.com/hzzzm/rdp-pool/server/internal/domain"
)

type MySQL struct {
	DB *sql.DB
}

func OpenMySQL(dsn string) (*MySQL, error) {
	return OpenMySQLRetry(dsn, 30, time.Second)
}

// OpenMySQLRetry pings with backoff so compose/Docker Desktop startup races are tolerated.
func OpenMySQLRetry(dsn string, attempts int, delay time.Duration) (*MySQL, error) {
	db, err := sql.Open("mysql", dsn)
	if err != nil {
		return nil, err
	}
	db.SetMaxOpenConns(20)
	db.SetMaxIdleConns(5)
	db.SetConnMaxLifetime(30 * time.Minute)

	var last error
	for i := 1; i <= attempts; i++ {
		ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		last = db.PingContext(ctx)
		cancel()
		if last == nil {
			return &MySQL{DB: db}, nil
		}
		time.Sleep(delay)
	}
	_ = db.Close()
	return nil, fmt.Errorf("mysql ping after %d attempts: %w", attempts, last)
}

func (m *MySQL) Close() error {
	if m.DB == nil {
		return nil
	}
	return m.DB.Close()
}

func (m *MySQL) GetUserByLogin(ctx context.Context, login string) (*domain.PoolUser, error) {
	row := m.DB.QueryRowContext(ctx, `
		SELECT id, login, password_hash, COALESCE(display_name,''), enabled
		FROM pool_users WHERE login = ? LIMIT 1`, login)
	var u domain.PoolUser
	var enabled int
	if err := row.Scan(&u.ID, &u.Login, &u.PasswordHash, &u.DisplayName, &enabled); err != nil {
		return nil, err
	}
	u.Enabled = enabled == 1
	return &u, nil
}

func (m *MySQL) ListAvailableGroups(ctx context.Context, userID string) ([]domain.AvailableGroup, error) {
	rows, err := m.DB.QueryContext(ctx, `
		SELECT g.id, g.name,
		  COALESCE(SUM(CASE WHEN r.status='idle' AND r.enabled=1 THEN 1 ELSE 0 END),0) AS idle_count,
		  COALESCE(SUM(CASE WHEN r.status IN ('allocated','busy') THEN 1 ELSE 0 END),0) AS busy_count
		FROM user_group_grants ug
		JOIN resource_groups g ON g.id = ug.group_id AND g.enabled = 1
		LEFT JOIN resources r ON r.group_id = g.id AND r.enabled = 1
		WHERE ug.user_id = ?
		GROUP BY g.id, g.name, g.sort_order
		ORDER BY g.sort_order ASC, g.name ASC`, userID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []domain.AvailableGroup
	for rows.Next() {
		var g domain.AvailableGroup
		if err := rows.Scan(&g.ID, &g.Name, &g.IdleCount, &g.BusyCount); err != nil {
			return nil, err
		}
		out = append(out, g)
	}
	return out, rows.Err()
}

func (m *MySQL) HasGroupGrant(ctx context.Context, userID, groupID string) (bool, error) {
	var n int
	err := m.DB.QueryRowContext(ctx, `
		SELECT COUNT(1) FROM user_group_grants ug
		JOIN resource_groups g ON g.id = ug.group_id AND g.enabled = 1
		WHERE ug.user_id = ? AND ug.group_id = ?`, userID, groupID).Scan(&n)
	return n > 0, err
}

func (m *MySQL) GetActiveSessionByUser(ctx context.Context, userID string) (*domain.Session, error) {
	row := m.DB.QueryRowContext(ctx, `
		SELECT s.id, s.user_id, s.group_id, COALESCE(g.name,''), s.resource_id, s.state,
		       s.close_reason, s.lease_expires_at, s.occupation_deadline_at,
		       COALESCE(s.client_instance_id,''), s.rdp_connected, s.created_at, s.closed_at
		FROM sessions s
		LEFT JOIN resource_groups g ON g.id = s.group_id
		WHERE s.user_id = ? AND s.state IN ('pending','active')
		LIMIT 1`, userID)
	return scanSession(row)
}

func (m *MySQL) GetSession(ctx context.Context, id string) (*domain.Session, error) {
	row := m.DB.QueryRowContext(ctx, `
		SELECT s.id, s.user_id, s.group_id, COALESCE(g.name,''), s.resource_id, s.state,
		       s.close_reason, s.lease_expires_at, s.occupation_deadline_at,
		       COALESCE(s.client_instance_id,''), s.rdp_connected, s.created_at, s.closed_at
		FROM sessions s
		LEFT JOIN resource_groups g ON g.id = s.group_id
		WHERE s.id = ? LIMIT 1`, id)
	return scanSession(row)
}

func scanSession(row *sql.Row) (*domain.Session, error) {
	var s domain.Session
	var closeReason sql.NullString
	var lease, occ, closed sql.NullTime
	var rdp int
	if err := row.Scan(
		&s.ID, &s.UserID, &s.GroupID, &s.GroupName, &s.ResourceID, &s.State,
		&closeReason, &lease, &occ, &s.ClientInstanceID, &rdp, &s.CreatedAt, &closed,
	); err != nil {
		return nil, err
	}
	if closeReason.Valid {
		s.CloseReason = &closeReason.String
	}
	if lease.Valid {
		s.LeaseExpiresAt = &lease.Time
	}
	if occ.Valid {
		s.OccupationDeadlineAt = &occ.Time
	}
	if closed.Valid {
		s.ClosedAt = &closed.Time
	}
	s.RDPConnected = rdp == 1
	return &s, nil
}

func (m *MySQL) GetSetting(ctx context.Context, key string) (string, error) {
	var v string
	err := m.DB.QueryRowContext(ctx, `SELECT value FROM pool_settings WHERE `+"`key`"+` = ?`, key).Scan(&v)
	return v, err
}

func (m *MySQL) ListSettings(ctx context.Context) (map[string]string, error) {
	rows, err := m.DB.QueryContext(ctx, `SELECT `+"`key`"+`, value FROM pool_settings`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[string]string{}
	for rows.Next() {
		var k, v string
		if err := rows.Scan(&k, &v); err != nil {
			return nil, err
		}
		out[k] = v
	}
	return out, rows.Err()
}

func (m *MySQL) UpsertSetting(ctx context.Context, key, value string) error {
	_, err := m.DB.ExecContext(ctx, `
		INSERT INTO pool_settings (`+"`key`"+`, value, updated_at) VALUES (?, ?, NOW(3))
		ON DUPLICATE KEY UPDATE value = VALUES(value), updated_at = VALUES(updated_at)`, key, value)
	return err
}

func (m *MySQL) InsertAudit(ctx context.Context, actorType, actorID, action, targetType, targetID, ip string, metaJSON []byte) error {
	_, err := m.DB.ExecContext(ctx, `
		INSERT INTO audit_logs (at, actor_type, actor_id, action, target_type, target_id, meta_json, ip)
		VALUES (NOW(3), ?, ?, ?, ?, ?, ?, ?)`,
		actorType, actorID, action, nullStr(targetType), nullStr(targetID), nullBytes(metaJSON), nullStr(ip))
	return err
}

func nullStr(s string) any {
	if s == "" {
		return nil
	}
	return s
}

func nullBytes(b []byte) any {
	if len(b) == 0 {
		return nil
	}
	return b
}

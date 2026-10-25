package service

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"time"

	"github.com/google/uuid"
	"github.com/hzzzm/rdp-pool/server/internal/auth"
	"github.com/hzzzm/rdp-pool/server/internal/config"
	"github.com/hzzzm/rdp-pool/server/internal/domain"
	"github.com/hzzzm/rdp-pool/server/internal/store"
	"github.com/hzzzm/rdp-pool/server/internal/vault"
)

var (
	ErrUnauthorized = errors.New("unauthorized")
	ErrForbidden    = errors.New("forbidden")
	ErrConflict     = errors.New("conflict")
	ErrNotFound     = errors.New("not found")
	ErrGone         = errors.New("gone")
	ErrBadRequest   = errors.New("bad request")
)

type PoolService struct {
	Store  *store.MySQL
	Tokens *auth.TokenService
	Cfg    config.Config
}

func NewPoolService(st *store.MySQL, tokens *auth.TokenService, cfg config.Config) *PoolService {
	return &PoolService{Store: st, Tokens: tokens, Cfg: cfg}
}

func newID(prefix string) string {
	return prefix + "_" + uuid.NewString()[:8] + uuid.NewString()[9:13]
}

type LoginResult struct {
	AccessToken string    `json:"access_token"`
	ExpiresAt   time.Time `json:"expires_at"`
	User        struct {
		ID    string `json:"id"`
		Login string `json:"login"`
	} `json:"user"`
}

func (s *PoolService) Login(ctx context.Context, login, password string) (*LoginResult, error) {
	u, err := s.Store.GetUserByLogin(ctx, login)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, ErrUnauthorized
		}
		return nil, err
	}
	if !u.Enabled || !auth.CheckPassword(u.PasswordHash, password) {
		return nil, ErrUnauthorized
	}
	tok, exp, err := s.Tokens.IssuePoolUser(u.ID, u.Login)
	if err != nil {
		return nil, err
	}
	_ = s.Store.InsertAudit(ctx, "pool_user", u.ID, "login_ok", "pool_user", u.ID, "", nil)
	out := &LoginResult{AccessToken: tok, ExpiresAt: exp}
	out.User.ID = u.ID
	out.User.Login = u.Login
	return out, nil
}

func (s *PoolService) AvailableGroups(ctx context.Context, userID string) ([]domain.AvailableGroup, error) {
	groups, err := s.Store.ListAvailableGroups(ctx, userID)
	if err != nil {
		return nil, err
	}
	if groups == nil {
		groups = []domain.AvailableGroup{}
	}
	return groups, nil
}

type AcquireResult struct {
	Status    string              `json:"status"` // allocated | queued
	Session   *domain.Session     `json:"session,omitempty"`
	RDPTicket *domain.RDPTicket   `json:"rdp_ticket,omitempty"`
	Queue     *domain.QueueEntry  `json:"queue,omitempty"`
	Message   string              `json:"message,omitempty"`
}

// Acquire implements group-scoped allocation skeleton.
// Full SKIP LOCKED pick_idle / affinity / wait_queue logic lands in next iteration;
// this scaffold returns 202 queued stub or allocates first idle when available.
func (s *PoolService) Acquire(ctx context.Context, userID, groupID, clientInstanceID string) (*AcquireResult, error) {
	if groupID == "" {
		return nil, ErrBadRequest
	}
	ok, err := s.Store.HasGroupGrant(ctx, userID, groupID)
	if err != nil {
		return nil, err
	}
	if !ok {
		return nil, ErrForbidden
	}

	if cur, err := s.Store.GetActiveSessionByUser(ctx, userID); err == nil && cur != nil {
		if cur.GroupID != groupID {
			return nil, ErrConflict
		}
		ticket, _ := s.issueTicketStub(ctx, cur)
		return &AcquireResult{Status: "allocated", Session: cur, RDPTicket: ticket, Message: "idempotent existing session"}, nil
	} else if err != nil && !errors.Is(err, sql.ErrNoRows) {
		return nil, err
	}

	tx, err := s.Store.DB.BeginTx(ctx, nil)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback() }()

	var resourceID, host string
	var port int
	err = tx.QueryRowContext(ctx, `
		SELECT id, host, rdp_port FROM resources
		WHERE group_id = ? AND status = 'idle' AND enabled = 1
		ORDER BY
		  CASE supply_class WHEN 'clean' THEN 0 WHEN 'linger' THEN 1 ELSE 2 END,
		  supply_seq ASC
		LIMIT 1
		FOR UPDATE SKIP LOCKED`, groupID).Scan(&resourceID, &host, &port)
	if errors.Is(err, sql.ErrNoRows) {
		// Enqueue stub
		qeID := newID("que")
		var maxSeq sql.NullInt64
		_ = tx.QueryRowContext(ctx, `SELECT MAX(seq) FROM queue_entries WHERE group_id = ? AND status IN ('waiting','offered')`, groupID).Scan(&maxSeq)
		seq := int64(1)
		if maxSeq.Valid {
			seq = maxSeq.Int64 + 1
		}
		_, err = tx.ExecContext(ctx, `
			INSERT INTO queue_entries (id, group_id, user_id, status, seq, created_at, updated_at)
			VALUES (?, ?, ?, 'waiting', ?, NOW(3), NOW(3))`, qeID, groupID, userID, seq)
		if err != nil {
			return nil, err
		}
		if err := tx.Commit(); err != nil {
			return nil, err
		}
		ahead := int(seq - 1)
		return &AcquireResult{
			Status: "queued",
			Queue: &domain.QueueEntry{
				ID: qeID, GroupID: groupID, UserID: userID, Status: domain.QueueWaiting,
				Seq: seq, Position: int(seq), Ahead: ahead,
			},
			Message: fmt.Sprintf("queued in group; ahead=%d", ahead),
		}, nil
	}
	if err != nil {
		return nil, err
	}

	sessionID := newID("ses")
	now := time.Now()
	lease := now.Add(s.Cfg.LeaseTTL)
	occ := now.Add(s.Cfg.MaxOccupationTTL)
	_, err = tx.ExecContext(ctx, `
		INSERT INTO sessions (id, user_id, group_id, resource_id, state, lease_expires_at, occupation_deadline_at, client_instance_id, created_at)
		VALUES (?, ?, ?, ?, 'pending', ?, ?, ?, ?)`,
		sessionID, userID, groupID, resourceID, lease, occ, clientInstanceID, now)
	if err != nil {
		return nil, err
	}
	_, err = tx.ExecContext(ctx, `
		UPDATE resources SET status='allocated', current_session_id=?, supply_class=NULL, supply_seq=NULL, updated_at=NOW(3)
		WHERE id = ?`, sessionID, resourceID)
	if err != nil {
		return nil, err
	}
	if err := tx.Commit(); err != nil {
		return nil, err
	}

	sess := &domain.Session{
		ID: sessionID, UserID: userID, GroupID: groupID, ResourceID: resourceID,
		State: domain.SessionPending, LeaseExpiresAt: &lease, OccupationDeadlineAt: &occ,
		ClientInstanceID: clientInstanceID, CreatedAt: now,
	}
	ticket, _ := s.issueTicket(ctx, sessionID, resourceID, host, port)
	meta, _ := json.Marshal(map[string]any{"resource_id": resourceID})
	_ = s.Store.InsertAudit(ctx, "pool_user", userID, "acquire_allocated", "session", sessionID, "", meta)
	return &AcquireResult{Status: "allocated", Session: sess, RDPTicket: ticket}, nil
}

func (s *PoolService) issueTicket(ctx context.Context, sessionID, resourceID, host string, port int) (*domain.RDPTicket, error) {
	tkt := &domain.RDPTicket{TicketID: newID("tkt"), Host: host, Port: port}
	var blob []byte
	err := s.Store.DB.QueryRowContext(ctx, `SELECT ciphertext FROM resource_secrets WHERE resource_id = ?`, resourceID).Scan(&blob)
	if err == nil && len(blob) > 0 {
		sec, derr := vault.DecryptRDPSecret([]byte(s.Cfg.CredentialAESKey), blob)
		if derr == nil && sec != nil {
			tkt.Username = sec.Username
			tkt.Password = sec.Password // short-lived wire only; never log plaintext
		}
	} else if err != nil && !errors.Is(err, sql.ErrNoRows) {
		return nil, err
	}
	_, _ = s.Store.DB.ExecContext(ctx, `
		INSERT INTO rdp_tickets (id, session_id, expires_at, created_at)
		VALUES (?, ?, DATE_ADD(NOW(3), INTERVAL ? SECOND), NOW(3))`,
		tkt.TicketID, sessionID, int(s.Cfg.TicketTTL.Seconds()))
	// Audit without password
	meta, _ := json.Marshal(map[string]any{
		"ticket_id": tkt.TicketID, "host": host, "port": port,
		"username": tkt.Username, "has_password": tkt.Password != "",
	})
	_ = s.Store.InsertAudit(ctx, "system", "ticket", "ticket_issued", "session", sessionID, "", meta)
	return tkt, nil
}

func (s *PoolService) issueTicketStub(ctx context.Context, sess *domain.Session) (*domain.RDPTicket, error) {
	var host string
	var port int
	err := s.Store.DB.QueryRowContext(ctx, `SELECT host, rdp_port FROM resources WHERE id = ?`, sess.ResourceID).Scan(&host, &port)
	if err != nil {
		return nil, err
	}
	return s.issueTicket(ctx, sess.ID, sess.ResourceID, host, port)
}

// IssueSessionTicket re-issues a short-lived RDP connection ticket for an active/pending session.
func (s *PoolService) IssueSessionTicket(ctx context.Context, userID, sessionID string) (*domain.RDPTicket, error) {
	sess, err := s.Store.GetSession(ctx, sessionID)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, ErrNotFound
		}
		return nil, err
	}
	if sess.UserID != userID {
		return nil, ErrForbidden
	}
	if sess.State != domain.SessionPending && sess.State != domain.SessionActive {
		return nil, ErrGone
	}
	return s.issueTicketStub(ctx, sess)
}

func (s *PoolService) Heartbeat(ctx context.Context, userID, sessionID string, rdpConnected bool) (*domain.Session, error) {
	sess, err := s.Store.GetSession(ctx, sessionID)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, ErrNotFound
		}
		return nil, err
	}
	if sess.UserID != userID {
		return nil, ErrForbidden
	}
	if sess.State != domain.SessionPending && sess.State != domain.SessionActive {
		return nil, ErrGone
	}
	lease := time.Now().Add(s.Cfg.LeaseTTL)
	newState := domain.SessionActive
	_, err = s.Store.DB.ExecContext(ctx, `
		UPDATE sessions SET state=?, lease_expires_at=?, rdp_connected=? WHERE id=?`,
		newState, lease, boolToInt(rdpConnected), sessionID)
	if err != nil {
		return nil, err
	}
	_, _ = s.Store.DB.ExecContext(ctx, `UPDATE resources SET status='busy', updated_at=NOW(3) WHERE id=? AND current_session_id=?`,
		sess.ResourceID, sessionID)

	// Supersede prior soft-closed sessions on same resource when new login confirms RDP (§3.4)
	if rdpConnected {
		_, _ = s.Store.DB.ExecContext(ctx, `
			UPDATE sessions SET state='closed_superseded', close_reason='superseded_by_new_login', closed_at=COALESCE(closed_at, NOW(3))
			WHERE resource_id = ? AND id <> ? AND state = 'closed_soft'`, sess.ResourceID, sessionID)
		_ = s.Store.InsertAudit(ctx, "system", "reaper", "windows_session_superseded", "session", sessionID, "", nil)
	}

	sess.State = newState
	sess.LeaseExpiresAt = &lease
	sess.RDPConnected = rdpConnected
	return sess, nil
}

func (s *PoolService) Release(ctx context.Context, userID, sessionID string) (*domain.SoftReleaseResult, error) {
	sess, err := s.Store.GetSession(ctx, sessionID)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, ErrNotFound
		}
		return nil, err
	}
	if sess.UserID != userID {
		return nil, ErrForbidden
	}
	if sess.State != domain.SessionPending && sess.State != domain.SessionActive {
		return nil, ErrGone
	}
	return s.softRelease(ctx, sess, domain.ReasonUserRelease, "pool_user", userID)
}

func (s *PoolService) AdminSoftRelease(ctx context.Context, adminID, resourceID string) (*domain.SoftReleaseResult, error) {
	var sessionID sql.NullString
	err := s.Store.DB.QueryRowContext(ctx, `SELECT current_session_id FROM resources WHERE id = ?`, resourceID).Scan(&sessionID)
	if errors.Is(err, sql.ErrNoRows) {
		return nil, ErrNotFound
	}
	if err != nil {
		return nil, err
	}
	if !sessionID.Valid || sessionID.String == "" {
		return &domain.SoftReleaseResult{
			ResourceID: resourceID, CloseReason: domain.ReasonAdminSoftRelease,
			RDPDisconnected: false, LingerHint: false, SupplyClass: domain.SupplyLinger,
			IdleSupplyPosition: "tail", WaitersOffered: false,
			Message: "resource already idle; no-op",
		}, nil
	}
	sess, err := s.Store.GetSession(ctx, sessionID.String)
	if err != nil {
		return nil, err
	}
	return s.softRelease(ctx, sess, domain.ReasonAdminSoftRelease, "admin", adminID)
}

func (s *PoolService) softRelease(ctx context.Context, sess *domain.Session, reason, actorType, actorID string) (*domain.SoftReleaseResult, error) {
	tx, err := s.Store.DB.BeginTx(ctx, nil)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback() }()

	clean := domain.IsCleanRelease(reason)
	supplyClass := domain.SupplyLinger
	position := "tail"
	linger := true
	if clean {
		supplyClass = domain.SupplyClean
		position = "head"
		linger = false
	}

	var supplySeq int64
	if clean {
		var minSeq sql.NullInt64
		_ = tx.QueryRowContext(ctx, `
			SELECT MIN(supply_seq) FROM resources WHERE group_id = ? AND status = 'idle'`, sess.GroupID).Scan(&minSeq)
		if minSeq.Valid {
			supplySeq = minSeq.Int64 - 1
		} else {
			supplySeq = 0
		}
	} else {
		var maxSeq sql.NullInt64
		_ = tx.QueryRowContext(ctx, `
			SELECT MAX(supply_seq) FROM resources WHERE group_id = ? AND status = 'idle'`, sess.GroupID).Scan(&maxSeq)
		if maxSeq.Valid {
			supplySeq = maxSeq.Int64 + 1
		} else {
			supplySeq = 0
		}
	}

	_, err = tx.ExecContext(ctx, `
		UPDATE sessions SET state='closed_soft', close_reason=?, closed_at=NOW(3) WHERE id=?`, reason, sess.ID)
	if err != nil {
		return nil, err
	}
	_, err = tx.ExecContext(ctx, `
		UPDATE resources SET status='idle', current_session_id=NULL, supply_class=?, supply_seq=?,
		  linger_hint=?, last_soft_release_at=NOW(3), updated_at=NOW(3)
		WHERE id=?`, supplyClass, supplySeq, boolToInt(linger), sess.ResourceID)
	if err != nil {
		return nil, err
	}

	waitersOffered := false
	if clean {
		// try_offer_waiters stub: mark offered if waiter exists (claim path to be filled)
		var waiterID string
		err = tx.QueryRowContext(ctx, `
			SELECT id FROM queue_entries
			WHERE group_id=? AND status='waiting' ORDER BY seq ASC LIMIT 1 FOR UPDATE SKIP LOCKED`, sess.GroupID).Scan(&waiterID)
		if err == nil {
			_, _ = tx.ExecContext(ctx, `
				UPDATE queue_entries SET status='offered', offered_resource_id=?, offered_until=DATE_ADD(NOW(3), INTERVAL ? SECOND), updated_at=NOW(3)
				WHERE id=?`, sess.ResourceID, int(s.Cfg.OfferTTL.Seconds()), waiterID)
			waitersOffered = true
		} else if !errors.Is(err, sql.ErrNoRows) {
			return nil, err
		}
	}

	if reason == domain.ReasonUserRelease {
		_, _ = tx.ExecContext(ctx, `
			INSERT INTO user_group_affinities (user_id, group_id, last_resource_id, updated_at)
			VALUES (?, ?, ?, NOW(3))
			ON DUPLICATE KEY UPDATE last_resource_id=VALUES(last_resource_id), updated_at=VALUES(updated_at)`,
			sess.UserID, sess.GroupID, sess.ResourceID)
	}

	if err := tx.Commit(); err != nil {
		return nil, err
	}

	prev := sess.ID
	msg := "池名额已释放"
	if clean {
		msg = "用户主动释放：已入供给队首(clean)，可唤醒等待队首；未断开 RDP"
	} else {
		msg = "已回收池名额并排入供给队尾(linger)；未断开 RDP；未立即唤醒排队用户"
	}
	meta, _ := json.Marshal(map[string]any{
		"close_reason": reason, "supply_class": supplyClass, "waiters_offered": waitersOffered,
	})
	_ = s.Store.InsertAudit(ctx, actorType, actorID, "soft_release", "session", sess.ID, "", meta)

	return &domain.SoftReleaseResult{
		ResourceID: sess.ResourceID, PreviousSessionID: &prev, CloseReason: reason,
		RDPDisconnected: false, LingerHint: linger, SupplyClass: supplyClass,
		IdleSupplyPosition: position, WaitersOffered: waitersOffered, Message: msg,
	}, nil
}

func (s *PoolService) QueueMe(ctx context.Context, userID string) (*domain.QueueEntry, error) {
	row := s.Store.DB.QueryRowContext(ctx, `
		SELECT id, group_id, user_id, status, seq, offered_resource_id, offered_until
		FROM queue_entries WHERE user_id=? AND status IN ('waiting','offered')
		ORDER BY created_at DESC LIMIT 1`, userID)
	var q domain.QueueEntry
	var offeredRes sql.NullString
	var offeredUntil sql.NullTime
	if err := row.Scan(&q.ID, &q.GroupID, &q.UserID, &q.Status, &q.Seq, &offeredRes, &offeredUntil); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, ErrNotFound
		}
		return nil, err
	}
	if offeredRes.Valid {
		q.OfferedResourceID = &offeredRes.String
	}
	if offeredUntil.Valid {
		q.OfferedUntil = &offeredUntil.Time
	}
	var ahead int
	_ = s.Store.DB.QueryRowContext(ctx, `
		SELECT COUNT(1) FROM queue_entries
		WHERE group_id=? AND status='waiting' AND seq < ?`, q.GroupID, q.Seq).Scan(&ahead)
	q.Ahead = ahead
	q.Position = ahead + 1
	return &q, nil
}

func (s *PoolService) CancelQueue(ctx context.Context, userID string) error {
	res, err := s.Store.DB.ExecContext(ctx, `
		UPDATE queue_entries SET status='canceled', updated_at=NOW(3)
		WHERE user_id=? AND status IN ('waiting','offered')`, userID)
	if err != nil {
		return err
	}
	n, _ := res.RowsAffected()
	if n == 0 {
		return ErrNotFound
	}
	return nil
}

func boolToInt(b bool) int {
	if b {
		return 1
	}
	return 0
}

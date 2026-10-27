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
			INSERT INTO queue_entries (id, group_id, user_id, status, seq, created_at, updated_at, heartbeat_at)
			VALUES (?, ?, ?, 'waiting', ?, NOW(3), NOW(3), NOW(3))`, qeID, groupID, userID, seq)
		if err != nil {
			return nil, err
		}
		if err := tx.Commit(); err != nil {
			return nil, err
		}
		meta, _ := json.Marshal(map[string]any{"group_id": groupID, "seq": seq})
		_ = s.Store.InsertAudit(ctx, "pool_user", userID, "queue_start", "queue_entry", qeID, "", meta)
		ahead := int(seq - 1)
		return &AcquireResult{
			Status: "queued",
			Queue: &domain.QueueEntry{
				ID: qeID, GroupID: groupID, UserID: userID, Status: domain.QueueWaiting,
				Seq: seq, Position: int(seq), Ahead: ahead, CreatedAt: time.Now(),
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

	waitersOffered := false
	if clean {
		var waiterID string
		err = tx.QueryRowContext(ctx, `
			SELECT id FROM queue_entries
			WHERE group_id=? AND status='waiting' ORDER BY seq ASC LIMIT 1 FOR UPDATE SKIP LOCKED`, sess.GroupID).Scan(&waiterID)
		if err == nil {
			// Reserve machine for waiter (not general idle) until claim / offer TTL.
			_, err = tx.ExecContext(ctx, `
				UPDATE resources SET status='allocated', current_session_id=NULL, supply_class=NULL, supply_seq=NULL,
				  linger_hint=0, last_soft_release_at=NOW(3), updated_at=NOW(3)
				WHERE id=?`, sess.ResourceID)
			if err != nil {
				return nil, err
			}
			_, err = tx.ExecContext(ctx, `
				UPDATE queue_entries SET status='offered', offered_resource_id=?, offered_until=DATE_ADD(NOW(3), INTERVAL ? SECOND), updated_at=NOW(3)
				WHERE id=?`, sess.ResourceID, int(s.Cfg.OfferTTL.Seconds()), waiterID)
			if err != nil {
				return nil, err
			}
			waitersOffered = true
			position = "offered"
			supplyClass = ""
		} else if !errors.Is(err, sql.ErrNoRows) {
			return nil, err
		}
	}
	if !waitersOffered {
		_, err = tx.ExecContext(ctx, `
			UPDATE resources SET status='idle', current_session_id=NULL, supply_class=?, supply_seq=?,
			  linger_hint=?, last_soft_release_at=NOW(3), updated_at=NOW(3)
			WHERE id=?`, supplyClass, supplySeq, boolToInt(linger), sess.ResourceID)
		if err != nil {
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
		SELECT id, group_id, user_id, status, seq, offered_resource_id, offered_until, created_at
		FROM queue_entries WHERE user_id=? AND status IN ('waiting','offered')
		ORDER BY created_at DESC LIMIT 1`, userID)
	var q domain.QueueEntry
	var offeredRes sql.NullString
	var offeredUntil sql.NullTime
	if err := row.Scan(&q.ID, &q.GroupID, &q.UserID, &q.Status, &q.Seq, &offeredRes, &offeredUntil, &q.CreatedAt); err != nil {
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
	now := time.Now()
	_, _ = s.Store.DB.ExecContext(ctx, `
		UPDATE queue_entries SET heartbeat_at=?, updated_at=? WHERE id=? AND status IN ('waiting','offered')`,
		now, now, q.ID)
	q.HeartbeatAt = &now
	q.WaitSec = int(now.Sub(q.CreatedAt).Seconds())
	var ahead int
	_ = s.Store.DB.QueryRowContext(ctx, `
		SELECT COUNT(1) FROM queue_entries
		WHERE group_id=? AND status='waiting' AND seq < ?`, q.GroupID, q.Seq).Scan(&ahead)
	q.Ahead = ahead
	q.Position = ahead + 1
	return &q, nil
}

func (s *PoolService) CancelQueue(ctx context.Context, userID string) error {
	return s.finishQueue(ctx, userID, "", domain.QueueCanceled, domain.QueueOutcomeCanceled, "pool_user", userID)
}

// finishQueue ends the caller's active queue entry (or a specific id) and writes history.
func (s *PoolService) finishQueue(ctx context.Context, userID, queueID, newStatus, outcome, actorType, actorID string) error {
	tx, err := s.Store.DB.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	var qID, groupID, status string
	var offeredRes sql.NullString
	var createdAt time.Time
	if queueID != "" {
		err = tx.QueryRowContext(ctx, `
			SELECT id, group_id, user_id, status, offered_resource_id, created_at FROM queue_entries
			WHERE id=? AND status IN ('waiting','offered') FOR UPDATE`, queueID).Scan(
			&qID, &groupID, &userID, &status, &offeredRes, &createdAt)
	} else {
		err = tx.QueryRowContext(ctx, `
			SELECT id, group_id, status, offered_resource_id, created_at FROM queue_entries
			WHERE user_id=? AND status IN ('waiting','offered')
			ORDER BY created_at DESC LIMIT 1 FOR UPDATE`, userID).Scan(&qID, &groupID, &status, &offeredRes, &createdAt)
	}
	if errors.Is(err, sql.ErrNoRows) {
		return ErrNotFound
	}
	if err != nil {
		return err
	}
	now := time.Now()
	waitSec := int(now.Sub(createdAt).Seconds())
	if waitSec < 0 {
		waitSec = 0
	}
	_, err = tx.ExecContext(ctx, `
		UPDATE queue_entries SET status=?, updated_at=?, ended_at=?, offered_resource_id=NULL, offered_until=NULL
		WHERE id=?`, newStatus, now, now, qID)
	if err != nil {
		return err
	}
	histID := newID("qH")
	_, err = tx.ExecContext(ctx, `
		INSERT INTO queue_history (id, queue_entry_id, group_id, user_id, started_at, ended_at, wait_sec, outcome)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
		histID, qID, groupID, userID, createdAt, now, waitSec, outcome)
	if err != nil {
		return err
	}
	// Release reserved offer machine back to idle supply (clean head) so others can take it.
	if status == domain.QueueOffered && offeredRes.Valid && offeredRes.String != "" {
		var minSeq sql.NullInt64
		_ = tx.QueryRowContext(ctx, `
			SELECT MIN(supply_seq) FROM resources WHERE group_id=? AND status='idle'`, groupID).Scan(&minSeq)
		seq := int64(0)
		if minSeq.Valid {
			seq = minSeq.Int64 - 1
		}
		_, _ = tx.ExecContext(ctx, `
			UPDATE resources SET status='idle', current_session_id=NULL, supply_class='clean', supply_seq=?,
			  linger_hint=0, updated_at=NOW(3)
			WHERE id=? AND status='allocated' AND current_session_id IS NULL`, seq, offeredRes.String)
	}
	if err := tx.Commit(); err != nil {
		return err
	}
	meta, _ := json.Marshal(map[string]any{
		"group_id": groupID, "outcome": outcome, "wait_sec": waitSec,
	})
	_ = s.Store.InsertAudit(ctx, actorType, actorID, "queue_end", "queue_entry", qID, "", meta)
	return nil
}

// Claim converts the caller's waiting/offered queue entry into a session + RDP ticket.
func (s *PoolService) Claim(ctx context.Context, userID, clientInstanceID string) (*AcquireResult, error) {
	if cur, err := s.Store.GetActiveSessionByUser(ctx, userID); err == nil && cur != nil {
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

	var q domain.QueueEntry
	var offeredRes sql.NullString
	var offeredUntil sql.NullTime
	err = tx.QueryRowContext(ctx, `
		SELECT id, group_id, user_id, status, seq, offered_resource_id, offered_until, created_at
		FROM queue_entries WHERE user_id=? AND status IN ('waiting','offered')
		ORDER BY created_at DESC LIMIT 1 FOR UPDATE`, userID).Scan(
		&q.ID, &q.GroupID, &q.UserID, &q.Status, &q.Seq, &offeredRes, &offeredUntil, &q.CreatedAt)
	if errors.Is(err, sql.ErrNoRows) {
		return nil, ErrNotFound
	}
	if err != nil {
		return nil, err
	}
	if offeredRes.Valid {
		q.OfferedResourceID = &offeredRes.String
	}
	// Refresh queue heartbeat while still waiting / claiming.
	_, _ = tx.ExecContext(ctx, `
		UPDATE queue_entries SET heartbeat_at=NOW(3), updated_at=NOW(3) WHERE id=?`, q.ID)

	var resourceID, host string
	var port int
	tryResource := func(rid string) error {
		return tx.QueryRowContext(ctx, `
			SELECT id, host, rdp_port FROM resources
			WHERE id=? AND group_id=? AND enabled=1
			  AND (status='idle' OR (status='allocated' AND current_session_id IS NULL))
			FOR UPDATE`, rid, q.GroupID).Scan(&resourceID, &host, &port)
	}
	pickIdle := func() error {
		return tx.QueryRowContext(ctx, `
			SELECT id, host, rdp_port FROM resources
			WHERE group_id=? AND status='idle' AND enabled=1
			ORDER BY
			  CASE supply_class WHEN 'clean' THEN 0 WHEN 'linger' THEN 1 ELSE 2 END,
			  supply_seq ASC
			LIMIT 1
			FOR UPDATE SKIP LOCKED`, q.GroupID).Scan(&resourceID, &host, &port)
	}

	got := false
	if q.Status == domain.QueueOffered && q.OfferedResourceID != nil {
		if err := tryResource(*q.OfferedResourceID); err == nil {
			got = true
		} else if !errors.Is(err, sql.ErrNoRows) {
			return nil, err
		}
	}
	if !got {
		if err := pickIdle(); err == nil {
			got = true
		} else if !errors.Is(err, sql.ErrNoRows) {
			return nil, err
		}
	}
	if !got {
		// Still waiting — keep queue entry; caller should keep polling.
		if err := tx.Commit(); err != nil {
			return nil, err
		}
		var ahead int
		_ = s.Store.DB.QueryRowContext(ctx, `
			SELECT COUNT(1) FROM queue_entries
			WHERE group_id=? AND status='waiting' AND seq < ?`, q.GroupID, q.Seq).Scan(&ahead)
		q.Ahead = ahead
		q.Position = ahead + 1
		return &AcquireResult{Status: "queued", Queue: &q, Message: "still queued; no idle yet"}, nil
	}

	sessionID := newID("ses")
	now := time.Now()
	lease := now.Add(s.Cfg.LeaseTTL)
	occ := now.Add(s.Cfg.MaxOccupationTTL)
	_, err = tx.ExecContext(ctx, `
		INSERT INTO sessions (id, user_id, group_id, resource_id, state, lease_expires_at, occupation_deadline_at, client_instance_id, created_at)
		VALUES (?, ?, ?, ?, 'pending', ?, ?, ?, ?)`,
		sessionID, userID, q.GroupID, resourceID, lease, occ, clientInstanceID, now)
	if err != nil {
		return nil, err
	}
	_, err = tx.ExecContext(ctx, `
		UPDATE resources SET status='allocated', current_session_id=?, supply_class=NULL, supply_seq=NULL, updated_at=NOW(3)
		WHERE id=?`, sessionID, resourceID)
	if err != nil {
		return nil, err
	}
	waitSec := int(now.Sub(q.CreatedAt).Seconds())
	if waitSec < 0 {
		waitSec = 0
	}
	_, err = tx.ExecContext(ctx, `
		UPDATE queue_entries SET status='claimed', offered_resource_id=NULL, offered_until=NULL,
		  updated_at=?, ended_at=? WHERE id=?`, now, now, q.ID)
	if err != nil {
		return nil, err
	}
	histID := newID("qH")
	_, err = tx.ExecContext(ctx, `
		INSERT INTO queue_history (id, queue_entry_id, group_id, user_id, started_at, ended_at, wait_sec, outcome)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
		histID, q.ID, q.GroupID, userID, q.CreatedAt, now, waitSec, domain.QueueOutcomeClaimed)
	if err != nil {
		return nil, err
	}
	if err := tx.Commit(); err != nil {
		return nil, err
	}

	sess := &domain.Session{
		ID: sessionID, UserID: userID, GroupID: q.GroupID, ResourceID: resourceID,
		State: domain.SessionPending, LeaseExpiresAt: &lease, OccupationDeadlineAt: &occ,
		ClientInstanceID: clientInstanceID, CreatedAt: now,
	}
	ticket, _ := s.issueTicket(ctx, sessionID, resourceID, host, port)
	meta, _ := json.Marshal(map[string]any{"resource_id": resourceID, "queue_id": q.ID, "wait_sec": waitSec})
	_ = s.Store.InsertAudit(ctx, "pool_user", userID, "queue_claimed", "session", sessionID, "", meta)
	return &AcquireResult{Status: "allocated", Session: sess, RDPTicket: ticket, Message: "claimed from queue"}, nil
}

// ListActiveQueues returns waiting/offered entries with group name and wait duration.
func (s *PoolService) ListActiveQueues(ctx context.Context) ([]domain.QueueEntry, error) {
	rows, err := s.Store.DB.QueryContext(ctx, `
		SELECT q.id, q.group_id, COALESCE(g.name,''), q.user_id, COALESCE(u.login,''),
		       q.status, q.seq, q.created_at, q.heartbeat_at
		FROM queue_entries q
		LEFT JOIN resource_groups g ON g.id = q.group_id
		LEFT JOIN pool_users u ON u.id = q.user_id
		WHERE q.status IN ('waiting','offered')
		ORDER BY q.group_id, q.seq`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	now := time.Now()
	var list []domain.QueueEntry
	for rows.Next() {
		var q domain.QueueEntry
		var hb sql.NullTime
		if err := rows.Scan(&q.ID, &q.GroupID, &q.GroupName, &q.UserID, &q.UserLogin,
			&q.Status, &q.Seq, &q.CreatedAt, &hb); err != nil {
			return nil, err
		}
		if hb.Valid {
			t := hb.Time
			q.HeartbeatAt = &t
		}
		q.WaitSec = int(now.Sub(q.CreatedAt).Seconds())
		if q.WaitSec < 0 {
			q.WaitSec = 0
		}
		list = append(list, q)
	}
	if list == nil {
		list = []domain.QueueEntry{}
	}
	return list, nil
}

// QueueStatsByGroup returns 7d/30d queue counts and average wait from queue_history.
func (s *PoolService) QueueStatsByGroup(ctx context.Context) ([]domain.QueueGroupStats, error) {
	rows, err := s.Store.DB.QueryContext(ctx, `
		SELECT g.id, g.name, g.code,
		  COALESCE(SUM(CASE WHEN h.ended_at >= DATE_SUB(NOW(3), INTERVAL 30 DAY) THEN 1 ELSE 0 END), 0) AS month_count,
		  COALESCE(AVG(CASE WHEN h.ended_at >= DATE_SUB(NOW(3), INTERVAL 30 DAY) THEN h.wait_sec END), 0) AS month_avg,
		  COALESCE(SUM(CASE WHEN h.ended_at >= DATE_SUB(NOW(3), INTERVAL 7 DAY) THEN 1 ELSE 0 END), 0) AS week_count,
		  COALESCE(AVG(CASE WHEN h.ended_at >= DATE_SUB(NOW(3), INTERVAL 7 DAY) THEN h.wait_sec END), 0) AS week_avg
		FROM resource_groups g
		LEFT JOIN queue_history h ON h.group_id = g.id
		  AND h.ended_at >= DATE_SUB(NOW(3), INTERVAL 30 DAY)
		GROUP BY g.id, g.name, g.code
		ORDER BY g.sort_order ASC, g.name ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var list []domain.QueueGroupStats
	for rows.Next() {
		var st domain.QueueGroupStats
		if err := rows.Scan(&st.GroupID, &st.GroupName, &st.GroupCode,
			&st.MonthCount, &st.MonthAvgWaitSec, &st.WeekCount, &st.WeekAvgWaitSec); err != nil {
			return nil, err
		}
		list = append(list, st)
	}
	if list == nil {
		list = []domain.QueueGroupStats{}
	}
	return list, nil
}

func boolToInt(b bool) int {
	if b {
		return 1
	}
	return 0
}

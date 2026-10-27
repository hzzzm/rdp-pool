package service

import (
	"context"
	"database/sql"
	"log"
	"time"

	"github.com/hzzzm/rdp-pool/server/internal/domain"
)

// StartReaper runs background lease + queue heartbeat reclaim loops until ctx is done.
func (s *PoolService) StartReaper(ctx context.Context) {
	ticker := time.NewTicker(5 * time.Second)
	go func() {
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				s.reapOnce(ctx)
			}
		}
	}()
}

func (s *PoolService) reapOnce(parent context.Context) {
	ctx, cancel := context.WithTimeout(parent, 8*time.Second)
	defer cancel()
	if err := s.reapExpiredSessions(ctx); err != nil {
		log.Printf("reaper sessions: %v", err)
	}
	if err := s.reapStaleQueues(ctx); err != nil {
		log.Printf("reaper queues: %v", err)
	}
}

func (s *PoolService) reapExpiredSessions(ctx context.Context) error {
	rows, err := s.Store.DB.QueryContext(ctx, `
		SELECT id, user_id, group_id, resource_id, state, lease_expires_at, occupation_deadline_at
		FROM sessions
		WHERE state IN ('pending','active')
		  AND (
		    (lease_expires_at IS NOT NULL AND lease_expires_at < NOW(3))
		    OR (occupation_deadline_at IS NOT NULL AND occupation_deadline_at < NOW(3))
		  )
		LIMIT 50`)
	if err != nil {
		return err
	}
	defer rows.Close()

	type row struct {
		id, userID, groupID, resourceID, state string
		lease, occ                             sql.NullTime
	}
	var list []row
	for rows.Next() {
		var r row
		if err := rows.Scan(&r.id, &r.userID, &r.groupID, &r.resourceID, &r.state, &r.lease, &r.occ); err != nil {
			return err
		}
		list = append(list, r)
	}
	for _, r := range list {
		reason := domain.ReasonLeaseExpired
		if r.occ.Valid && r.occ.Time.Before(time.Now()) {
			reason = domain.ReasonMaxOccupationTTL
		}
		sess := &domain.Session{
			ID: r.id, UserID: r.userID, GroupID: r.groupID, ResourceID: r.resourceID, State: r.state,
		}
		if _, err := s.softRelease(ctx, sess, reason, "system", "reaper"); err != nil {
			log.Printf("reaper soft_release %s: %v", r.id, err)
		}
	}
	return nil
}

// reapStaleQueues drops waiting/offered entries whose client stopped heartbeating
// (no queue/me or claim poll within QueueHeartbeatTTL).
func (s *PoolService) reapStaleQueues(ctx context.Context) error {
	ttl := s.Cfg.QueueHeartbeatTTL
	if ttl <= 0 {
		ttl = 45 * time.Second
	}
	cutoff := time.Now().Add(-ttl)
	rows, err := s.Store.DB.QueryContext(ctx, `
		SELECT id, user_id FROM queue_entries
		WHERE status IN ('waiting','offered')
		  AND COALESCE(heartbeat_at, updated_at, created_at) < ?
		LIMIT 100`, cutoff)
	if err != nil {
		return err
	}
	defer rows.Close()
	type item struct{ id, userID string }
	var list []item
	for rows.Next() {
		var it item
		if err := rows.Scan(&it.id, &it.userID); err != nil {
			return err
		}
		list = append(list, it)
	}
	for _, it := range list {
		if err := s.finishQueue(ctx, it.userID, it.id, domain.QueueTimedOut, domain.QueueOutcomeTimedOut, "system", "reaper"); err != nil {
			if err == ErrNotFound {
				continue
			}
			log.Printf("reaper queue %s: %v", it.id, err)
		}
	}
	return nil
}

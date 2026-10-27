package domain

import "time"

// Resource pool status (architecture §3.2)
const (
	ResourceIdle         = "idle"
	ResourceAllocated    = "allocated"
	ResourceBusy         = "busy"
	ResourceMaintenance  = "maintenance"
	ResourceDisabled     = "disabled"
)

// Session states (§3.3)
const (
	SessionPending           = "pending"
	SessionActive            = "active"
	SessionClosedSoft        = "closed_soft"
	SessionClosedSuperseded  = "closed_superseded"
	SessionClosedHard        = "closed_hard" // non-MVP
)

// Close reasons
const (
	ReasonUserRelease          = "user_release"
	ReasonLeaseExpired         = "lease_expired"
	ReasonAdminSoftRelease     = "admin_soft_release"
	ReasonMaxOccupationTTL     = "max_occupation_ttl"
	ReasonPendingTimeout       = "pending_timeout"
	ReasonSupersededByNewLogin = "superseded_by_new_login"
)

// Supply class on idle resources
const (
	SupplyClean  = "clean"
	SupplyLinger = "linger"
)

// Queue entry status (§3.5)
const (
	QueueWaiting   = "waiting"
	QueueOffered   = "offered"
	QueueClaimed   = "claimed"
	QueueCanceled  = "canceled"
	QueueTimedOut  = "timed_out"
)

// Queue end outcomes (persisted in queue_history)
const (
	QueueOutcomeCanceled = "canceled"
	QueueOutcomeClaimed  = "claimed"
	QueueOutcomeTimedOut = "timed_out"
)

type ResourceGroup struct {
	ID          string    `json:"id"`
	Code        string    `json:"code"`
	Name        string    `json:"name"`
	Enabled     bool      `json:"enabled"`
	SortOrder   int       `json:"sort_order"`
	Description string    `json:"description,omitempty"`
	CreatedAt   time.Time `json:"created_at"`
}

type AvailableGroup struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	IdleCount int    `json:"idle_count"`
	BusyCount int    `json:"busy_count"`
}

type PoolUser struct {
	ID           string `json:"id"`
	Login        string `json:"login"`
	PasswordHash string `json:"-"`
	DisplayName  string `json:"display_name,omitempty"`
	Enabled      bool   `json:"enabled"`
}

type Resource struct {
	ID                 string     `json:"id"`
	GroupID            string     `json:"group_id"`
	Name               string     `json:"name"`
	Host               string     `json:"host"`
	RDPPort            int        `json:"rdp_port"`
	Status             string     `json:"status"`
	CurrentSessionID   *string    `json:"current_session_id,omitempty"`
	SupplyClass        *string    `json:"supply_class,omitempty"`
	SupplySeq          *int64     `json:"supply_seq,omitempty"`
	LingerHint         bool       `json:"linger_hint"`
	LastSoftReleaseAt  *time.Time `json:"last_soft_release_at,omitempty"`
	Enabled            bool       `json:"enabled"`
}

type Session struct {
	ID                   string     `json:"id"`
	UserID               string     `json:"user_id"`
	GroupID              string     `json:"group_id"`
	GroupName            string     `json:"group_name,omitempty"`
	ResourceID           string     `json:"resource_id"`
	State                string     `json:"state"`
	CloseReason          *string    `json:"close_reason,omitempty"`
	LeaseExpiresAt       *time.Time `json:"lease_expires_at,omitempty"`
	OccupationDeadlineAt *time.Time `json:"occupation_deadline_at,omitempty"`
	ClientInstanceID     string     `json:"client_instance_id,omitempty"`
	RDPConnected         bool       `json:"rdp_connected"`
	CreatedAt            time.Time  `json:"created_at"`
	ClosedAt             *time.Time `json:"closed_at,omitempty"`
}

type QueueEntry struct {
	ID                 string     `json:"id"`
	GroupID            string     `json:"group_id"`
	GroupName          string     `json:"group_name,omitempty"`
	UserID             string     `json:"user_id"`
	UserLogin          string     `json:"user_login,omitempty"`
	Status             string     `json:"status"`
	Seq                int64      `json:"seq"`
	Position           int        `json:"position,omitempty"`
	Ahead              int        `json:"ahead,omitempty"`
	OfferedResourceID  *string    `json:"offered_resource_id,omitempty"`
	OfferedUntil       *time.Time `json:"offered_until,omitempty"`
	CreatedAt          time.Time  `json:"created_at,omitempty"`
	HeartbeatAt        *time.Time `json:"heartbeat_at,omitempty"`
	WaitSec            int        `json:"wait_sec,omitempty"`
}

type QueueGroupStats struct {
	GroupID           string  `json:"group_id"`
	GroupName         string  `json:"group_name"`
	GroupCode         string  `json:"group_code,omitempty"`
	MonthCount        int64   `json:"month_count"`
	MonthAvgWaitSec   float64 `json:"month_avg_wait_sec"`
	WeekCount         int64   `json:"week_count"`
	WeekAvgWaitSec    float64 `json:"week_avg_wait_sec"`
}

type RDPTicket struct {
	TicketID string `json:"ticket_id"`
	Host     string `json:"host"`
	Port     int    `json:"port"`
	Username string `json:"username"`
	Password string `json:"password"`
}

type SoftReleaseResult struct {
	ResourceID         string  `json:"resource_id"`
	PreviousSessionID  *string `json:"previous_session_id"`
	CloseReason        string  `json:"close_reason"`
	RDPDisconnected    bool    `json:"rdp_disconnected"`
	LingerHint         bool    `json:"linger_hint"`
	SupplyClass        string  `json:"supply_class"`
	IdleSupplyPosition string  `json:"idle_supply_position"`
	WaitersOffered     bool    `json:"waiters_offered"`
	Message            string  `json:"message"`
}

func IsCleanRelease(reason string) bool {
	return reason == ReasonUserRelease || reason == ReasonPendingTimeout
}

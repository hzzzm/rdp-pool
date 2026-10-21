package httpapi

import (
	"encoding/json"
	"errors"
	"net/http"
	"strings"

	"github.com/hzzzm/rdp-pool/server/internal/auth"
	"github.com/hzzzm/rdp-pool/server/internal/service"
	"github.com/hzzzm/rdp-pool/server/internal/store"
)

type Server struct {
	Pool   *service.PoolService
	Tokens *auth.TokenService
	Store  *store.MySQL
	Mux    *http.ServeMux
}

func New(pool *service.PoolService, tokens *auth.TokenService, st *store.MySQL) *Server {
	s := &Server{Pool: pool, Tokens: tokens, Store: st, Mux: http.NewServeMux()}
	s.routes()
	return s
}

func (s *Server) Handler() http.Handler {
	return cors(s.Mux)
}

func (s *Server) routes() {
	s.Mux.HandleFunc("GET /healthz", s.handleHealth)

	// Terminal user API
	s.Mux.HandleFunc("POST /api/v1/auth/login", s.handleLogin)
	s.Mux.HandleFunc("POST /api/v1/auth/refresh", s.handleRefreshStub)
	s.Mux.HandleFunc("GET /api/v1/groups/available", s.withPoolUser(s.handleAvailableGroups))
	s.Mux.HandleFunc("POST /api/v1/sessions/acquire", s.withPoolUser(s.handleAcquire))
	s.Mux.HandleFunc("GET /api/v1/sessions/current", s.withPoolUser(s.handleCurrentSession))
	s.Mux.HandleFunc("POST /api/v1/sessions/{id}/heartbeat", s.withPoolUser(s.handleHeartbeat))
	s.Mux.HandleFunc("POST /api/v1/sessions/{id}/release", s.withPoolUser(s.handleRelease))
	s.Mux.HandleFunc("POST /api/v1/sessions/claim", s.withPoolUser(s.handleClaimStub))
	s.Mux.HandleFunc("GET /api/v1/queue/me", s.withPoolUser(s.handleQueueMe))
	s.Mux.HandleFunc("DELETE /api/v1/queue/me", s.withPoolUser(s.handleCancelQueue))
	s.Mux.HandleFunc("GET /api/v1/ws/queue", s.handleWSQueueStub)
	s.Mux.HandleFunc("POST /api/v1/sessions/{id}/ticket", s.withPoolUser(s.handleTicketStub))

	// Admin API (Nginx /pool-api → here; expects admin_token)
	s.Mux.HandleFunc("POST /api/v1/admin/auth/exchange", s.handleAdminExchangeStub)
	s.Mux.HandleFunc("GET /api/v1/admin/groups", s.withAdmin(s.handleAdminListGroups))
	s.Mux.HandleFunc("POST /api/v1/admin/groups", s.withAdmin(s.handleAdminCreateGroup))
	s.Mux.HandleFunc("GET /api/v1/admin/resources", s.withAdmin(s.handleAdminListResources))
	s.Mux.HandleFunc("POST /api/v1/admin/resources", s.withAdmin(s.handleAdminCreateResource))
	s.Mux.HandleFunc("POST /api/v1/admin/resources/{id}/soft-release", s.withAdmin(s.handleAdminSoftRelease))
	s.Mux.HandleFunc("GET /api/v1/admin/pool-users", s.withAdmin(s.handleAdminListUsers))
	s.Mux.HandleFunc("PUT /api/v1/admin/pool-users/{id}/groups", s.withAdmin(s.handleAdminGrantGroups))
	s.Mux.HandleFunc("GET /api/v1/admin/sessions", s.withAdmin(s.handleAdminListSessions))
	s.Mux.HandleFunc("GET /api/v1/admin/queues", s.withAdmin(s.handleAdminListQueues))
	s.Mux.HandleFunc("GET /api/v1/admin/settings", s.withAdmin(s.handleAdminGetSettings))
	s.Mux.HandleFunc("PUT /api/v1/admin/settings", s.withAdmin(s.handleAdminPutSettings))
	s.Mux.HandleFunc("GET /api/v1/admin/audit", s.withAdmin(s.handleAdminAudit))
}

type ctxKey string

const claimsKey ctxKey = "claims"

type handlerFunc func(w http.ResponseWriter, r *http.Request, claims *auth.Claims)

func (s *Server) withPoolUser(next handlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		claims, err := s.bearer(r, false)
		if err != nil {
			writeErr(w, http.StatusUnauthorized, "unauthorized")
			return
		}
		next(w, r, claims)
	}
}

func (s *Server) withAdmin(next handlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		claims, err := s.bearer(r, true)
		if err != nil {
			writeErr(w, http.StatusUnauthorized, "unauthorized")
			return
		}
		next(w, r, claims)
	}
}

func (s *Server) bearer(r *http.Request, admin bool) (*auth.Claims, error) {
	h := r.Header.Get("Authorization")
	if !strings.HasPrefix(h, "Bearer ") {
		return nil, errors.New("missing bearer")
	}
	tok := strings.TrimPrefix(h, "Bearer ")
	if admin {
		return s.Tokens.ParseAdmin(tok)
	}
	return s.Tokens.ParsePoolUser(tok)
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func writeErr(w http.ResponseWriter, status int, msg string) {
	writeJSON(w, status, map[string]string{"error": msg})
}

func mapSvcErr(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, service.ErrUnauthorized):
		writeErr(w, http.StatusUnauthorized, "unauthorized")
	case errors.Is(err, service.ErrForbidden):
		writeErr(w, http.StatusForbidden, "forbidden")
	case errors.Is(err, service.ErrConflict):
		writeErr(w, http.StatusConflict, "conflict")
	case errors.Is(err, service.ErrNotFound):
		writeErr(w, http.StatusNotFound, "not found")
	case errors.Is(err, service.ErrGone):
		writeErr(w, http.StatusGone, "session closed")
	case errors.Is(err, service.ErrBadRequest):
		writeErr(w, http.StatusBadRequest, "bad request")
	default:
		writeErr(w, http.StatusInternalServerError, "internal error")
	}
}

func cors(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Access-Control-Allow-Headers", "Authorization, Content-Type, X-RuoYi-Token")
		w.Header().Set("Access-Control-Allow-Methods", "GET,POST,PUT,DELETE,OPTIONS")
		if r.Method == http.MethodOptions {
			w.WriteHeader(http.StatusNoContent)
			return
		}
		next.ServeHTTP(w, r)
	})
}

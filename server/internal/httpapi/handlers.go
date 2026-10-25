package httpapi

import (
	"database/sql"
	"encoding/json"
	"errors"
	"net/http"
	"time"

	"github.com/google/uuid"
	"github.com/hzzzm/rdp-pool/server/internal/auth"
	"github.com/hzzzm/rdp-pool/server/internal/vault"
)

func (s *Server) handleHealth(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}

func (s *Server) handleLogin(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Login    string `json:"login"`
		Password string `json:"password"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeErr(w, http.StatusBadRequest, "invalid json")
		return
	}
	res, err := s.Pool.Login(r.Context(), body.Login, body.Password)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	writeJSON(w, http.StatusOK, res)
}

func (s *Server) handleRefreshStub(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusNotImplemented, map[string]string{
		"error": "refresh not implemented in scaffold; re-login",
	})
}

func (s *Server) handleAvailableGroups(w http.ResponseWriter, r *http.Request, claims *auth.Claims) {
	groups, err := s.Pool.AvailableGroups(r.Context(), claims.UserID)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"groups": groups})
}

func (s *Server) handleAcquire(w http.ResponseWriter, r *http.Request, claims *auth.Claims) {
	var body struct {
		GroupID          string `json:"group_id"`
		ClientInstanceID string `json:"client_instance_id"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeErr(w, http.StatusBadRequest, "invalid json")
		return
	}
	res, err := s.Pool.Acquire(r.Context(), claims.UserID, body.GroupID, body.ClientInstanceID)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	status := http.StatusOK
	if res.Status == "queued" {
		status = http.StatusAccepted
	}
	writeJSON(w, status, res)
}

func (s *Server) handleCurrentSession(w http.ResponseWriter, r *http.Request, claims *auth.Claims) {
	sess, err := s.Pool.Store.GetActiveSessionByUser(r.Context(), claims.UserID)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			writeJSON(w, http.StatusOK, map[string]any{"session": nil})
			return
		}
		mapSvcErr(w, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"session": sess})
}

func (s *Server) handleHeartbeat(w http.ResponseWriter, r *http.Request, claims *auth.Claims) {
	id := r.PathValue("id")
	var body struct {
		RDPConnected bool `json:"rdp_connected"`
	}
	_ = json.NewDecoder(r.Body).Decode(&body)
	sess, err := s.Pool.Heartbeat(r.Context(), claims.UserID, id, body.RDPConnected)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"session": sess})
}

func (s *Server) handleRelease(w http.ResponseWriter, r *http.Request, claims *auth.Claims) {
	id := r.PathValue("id")
	res, err := s.Pool.Release(r.Context(), claims.UserID, id)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	writeJSON(w, http.StatusOK, res)
}

func (s *Server) handleClaimStub(w http.ResponseWriter, r *http.Request, claims *auth.Claims) {
	writeJSON(w, http.StatusNotImplemented, map[string]string{
		"error": "claim path stub — queue offer → session conversion next",
	})
}

func (s *Server) handleQueueMe(w http.ResponseWriter, r *http.Request, claims *auth.Claims) {
	q, err := s.Pool.QueueMe(r.Context(), claims.UserID)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	writeJSON(w, http.StatusOK, q)
}

func (s *Server) handleCancelQueue(w http.ResponseWriter, r *http.Request, claims *auth.Claims) {
	if err := s.Pool.CancelQueue(r.Context(), claims.UserID); err != nil {
		mapSvcErr(w, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"status": "canceled"})
}

func (s *Server) handleWSQueueStub(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusNotImplemented, map[string]string{
		"error": "websocket queue stub — use GET /queue/me polling for now",
	})
}

func (s *Server) handleTicketStub(w http.ResponseWriter, r *http.Request, claims *auth.Claims) {
	writeJSON(w, http.StatusNotImplemented, map[string]string{
		"error": "ticket re-issue stub — credentials vault AES-GCM next",
	})
}

func (s *Server) handleAdminExchangeStub(w http.ResponseWriter, r *http.Request) {
	// §2.3 dual-token: validate RuoYi (shim) JWT, then issue Go admin_token.
	// When full Java RuoYi is online, swap parseRuoyiShimToken for Java JWT/pubkey check.
	var body struct {
		RuoyiToken string `json:"ruoyi_token"`
		AdminID    string `json:"admin_id"`
		Login      string `json:"login"`
	}
	_ = json.NewDecoder(r.Body).Decode(&body)
	ryTok := body.RuoyiToken
	if ryTok == "" {
		ryTok = r.Header.Get("X-RuoYi-Token")
	}
	if ryTok == "" {
		ryTok = s.ruoyiBearer(r)
	}
	login, ok := s.parseRuoyiShimToken(ryTok)
	if !ok {
		writeErr(w, http.StatusUnauthorized, "invalid ruoyi token")
		return
	}
	if body.Login != "" {
		login = body.Login
	}
	adminID := body.AdminID
	if adminID == "" {
		adminID = login
	}
	tok, exp, err := s.Tokens.IssueAdmin(adminID, login)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, "token error")
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"admin_token": tok,
		"expires_at":  exp,
	})
}

func (s *Server) handleAdminListGroups(w http.ResponseWriter, r *http.Request, _ *auth.Claims) {
	rows, err := s.Store.DB.QueryContext(r.Context(), `
		SELECT id, code, name, enabled, sort_order, COALESCE(description,''), created_at
		FROM resource_groups ORDER BY sort_order, name`)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	defer rows.Close()
	type g struct {
		ID, Code, Name, Description string
		Enabled                     bool
		SortOrder                   int
		CreatedAt                   time.Time
	}
	var list []g
	for rows.Next() {
		var item g
		var en int
		if err := rows.Scan(&item.ID, &item.Code, &item.Name, &en, &item.SortOrder, &item.Description, &item.CreatedAt); err != nil {
			mapSvcErr(w, err)
			return
		}
		item.Enabled = en == 1
		list = append(list, item)
	}
	if list == nil {
		list = []g{}
	}
	writeJSON(w, http.StatusOK, map[string]any{"groups": list})
}

func (s *Server) handleAdminCreateGroup(w http.ResponseWriter, r *http.Request, claims *auth.Claims) {
	var body struct {
		Code        string `json:"code"`
		Name        string `json:"name"`
		SortOrder   int    `json:"sort_order"`
		Description string `json:"description"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body.Code == "" || body.Name == "" {
		writeErr(w, http.StatusBadRequest, "code and name required")
		return
	}
	id := "grp_" + uuid.NewString()[:8]
	_, err := s.Store.DB.ExecContext(r.Context(), `
		INSERT INTO resource_groups (id, code, name, enabled, sort_order, description, created_at, updated_at)
		VALUES (?, ?, ?, 1, ?, ?, NOW(3), NOW(3))`, id, body.Code, body.Name, body.SortOrder, body.Description)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	_ = s.Store.InsertAudit(r.Context(), "admin", claims.UserID, "group_create", "group", id, "", nil)
	writeJSON(w, http.StatusCreated, map[string]string{"id": id})
}

func (s *Server) handleAdminListResources(w http.ResponseWriter, r *http.Request, _ *auth.Claims) {
	groupID := r.URL.Query().Get("group_id")
	q := `SELECT id, group_id, name, host, rdp_port, status, linger_hint, enabled FROM resources`
	args := []any{}
	if groupID != "" {
		q += ` WHERE group_id = ?`
		args = append(args, groupID)
	}
	q += ` ORDER BY name`
	rows, err := s.Store.DB.QueryContext(r.Context(), q, args...)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	defer rows.Close()
	type res struct {
		ID, GroupID, Name, Host, Status string
		RDPPort                         int
		LingerHint, Enabled             bool
	}
	var list []res
	for rows.Next() {
		var item res
		var linger, en int
		if err := rows.Scan(&item.ID, &item.GroupID, &item.Name, &item.Host, &item.RDPPort, &item.Status, &linger, &en); err != nil {
			mapSvcErr(w, err)
			return
		}
		item.LingerHint = linger == 1
		item.Enabled = en == 1
		list = append(list, item)
	}
	if list == nil {
		list = []res{}
	}
	writeJSON(w, http.StatusOK, map[string]any{"resources": list})
}

func (s *Server) handleAdminCreateResource(w http.ResponseWriter, r *http.Request, claims *auth.Claims) {
	var body struct {
		GroupID string `json:"group_id"`
		Name    string `json:"name"`
		Host    string `json:"host"`
		RDPPort int    `json:"rdp_port"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body.GroupID == "" || body.Host == "" {
		writeErr(w, http.StatusBadRequest, "group_id and host required")
		return
	}
	if body.RDPPort == 0 {
		body.RDPPort = 3389
	}
	if body.Name == "" {
		body.Name = body.Host
	}
	id := "res_" + uuid.NewString()[:8]
	_, err := s.Store.DB.ExecContext(r.Context(), `
		INSERT INTO resources (id, group_id, name, host, rdp_port, status, supply_class, supply_seq, linger_hint, enabled, created_at, updated_at)
		VALUES (?, ?, ?, ?, ?, 'idle', 'clean', 0, 0, 1, NOW(3), NOW(3))`,
		id, body.GroupID, body.Name, body.Host, body.RDPPort)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	_ = s.Store.InsertAudit(r.Context(), "admin", claims.UserID, "resource_create", "resource", id, "", nil)
	writeJSON(w, http.StatusCreated, map[string]string{"id": id})
}

func (s *Server) handleAdminSoftRelease(w http.ResponseWriter, r *http.Request, claims *auth.Claims) {
	id := r.PathValue("id")
	res, err := s.Pool.AdminSoftRelease(r.Context(), claims.UserID, id)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	writeJSON(w, http.StatusOK, res)
}

func (s *Server) handleAdminListUsers(w http.ResponseWriter, r *http.Request, _ *auth.Claims) {
	rows, err := s.Store.DB.QueryContext(r.Context(), `
		SELECT id, login, COALESCE(display_name,''), enabled FROM pool_users ORDER BY login`)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	defer rows.Close()
	type u struct {
		ID, Login, DisplayName string
		Enabled                bool
	}
	var list []u
	for rows.Next() {
		var item u
		var en int
		if err := rows.Scan(&item.ID, &item.Login, &item.DisplayName, &en); err != nil {
			mapSvcErr(w, err)
			return
		}
		item.Enabled = en == 1
		list = append(list, item)
	}
	if list == nil {
		list = []u{}
	}
	writeJSON(w, http.StatusOK, map[string]any{"users": list})
}

func (s *Server) handleAdminCreateUser(w http.ResponseWriter, r *http.Request, claims *auth.Claims) {
	var body struct {
		Login       string `json:"login"`
		Password    string `json:"password"`
		DisplayName string `json:"display_name"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body.Login == "" || body.Password == "" {
		writeErr(w, http.StatusBadRequest, "login and password required")
		return
	}
	hash, err := auth.HashPassword(body.Password)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	id := "usr_" + uuid.NewString()[:8]
	if body.DisplayName == "" {
		body.DisplayName = body.Login
	}
	_, err = s.Store.DB.ExecContext(r.Context(), `
		INSERT INTO pool_users (id, login, password_hash, display_name, enabled, created_at, updated_at)
		VALUES (?, ?, ?, ?, 1, NOW(3), NOW(3))`, id, body.Login, hash, body.DisplayName)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	_ = s.Store.InsertAudit(r.Context(), "admin", claims.UserID, "pool_user_create", "pool_user", id, "", nil)
	writeJSON(w, http.StatusCreated, map[string]string{"id": id})
}

func (s *Server) handleAdminPutCredentials(w http.ResponseWriter, r *http.Request, claims *auth.Claims) {
	id := r.PathValue("id")
	var body struct {
		Username string `json:"username"`
		Password string `json:"password"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body.Username == "" || body.Password == "" {
		writeErr(w, http.StatusBadRequest, "username and password required")
		return
	}
	key := []byte(s.Pool.Cfg.CredentialAESKey)
	blob, err := vault.EncryptJSON(key, vault.RDPSecret{Username: body.Username, Password: body.Password})
	if err != nil {
		writeErr(w, http.StatusInternalServerError, "encrypt error")
		return
	}
	_, err = s.Store.DB.ExecContext(r.Context(), `
		INSERT INTO resource_secrets (resource_id, ciphertext, key_version, updated_at)
		VALUES (?, ?, 1, NOW(3))
		ON DUPLICATE KEY UPDATE ciphertext=VALUES(ciphertext), updated_at=NOW(3)`, id, blob)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	_ = s.Store.InsertAudit(r.Context(), "admin", claims.UserID, "resource_credentials_put", "resource", id, "", nil)
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}

func (s *Server) handleAdminGetUserGroups(w http.ResponseWriter, r *http.Request, _ *auth.Claims) {
	userID := r.PathValue("id")
	rows, err := s.Store.DB.QueryContext(r.Context(), `
		SELECT group_id FROM user_group_grants WHERE user_id = ? ORDER BY group_id`, userID)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	defer rows.Close()
	ids := make([]string, 0)
	for rows.Next() {
		var gid string
		if err := rows.Scan(&gid); err != nil {
			mapSvcErr(w, err)
			return
		}
		ids = append(ids, gid)
	}
	if err := rows.Err(); err != nil {
		mapSvcErr(w, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"group_ids": ids})
}

func (s *Server) handleAdminGrantGroups(w http.ResponseWriter, r *http.Request, claims *auth.Claims) {
	userID := r.PathValue("id")
	var body struct {
		GroupIDs []string `json:"group_ids"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeErr(w, http.StatusBadRequest, "invalid json")
		return
	}
	if body.GroupIDs == nil {
		body.GroupIDs = []string{}
	}
	tx, err := s.Store.DB.BeginTx(r.Context(), nil)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	defer func() { _ = tx.Rollback() }()
	if _, err := tx.ExecContext(r.Context(), `DELETE FROM user_group_grants WHERE user_id=?`, userID); err != nil {
		mapSvcErr(w, err)
		return
	}
	for _, gid := range body.GroupIDs {
		if _, err := tx.ExecContext(r.Context(), `
			INSERT INTO user_group_grants (user_id, group_id, created_at) VALUES (?, ?, NOW(3))`, userID, gid); err != nil {
			mapSvcErr(w, err)
			return
		}
	}
	if err := tx.Commit(); err != nil {
		mapSvcErr(w, err)
		return
	}
	_ = s.Store.InsertAudit(r.Context(), "admin", claims.UserID, "user_grant_groups", "pool_user", userID, "", nil)
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}

func (s *Server) handleAdminListSessions(w http.ResponseWriter, r *http.Request, _ *auth.Claims) {
	groupID := r.URL.Query().Get("group_id")
	q := `SELECT id, user_id, group_id, resource_id, state, close_reason, created_at, closed_at FROM sessions`
	args := []any{}
	if groupID != "" {
		q += ` WHERE group_id = ?`
		args = append(args, groupID)
	}
	q += ` ORDER BY created_at DESC LIMIT 200`
	rows, err := s.Store.DB.QueryContext(r.Context(), q, args...)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	defer rows.Close()
	type sess struct {
		ID, UserID, GroupID, ResourceID, State string
		CloseReason                            *string
		CreatedAt                              time.Time
		ClosedAt                               *time.Time
	}
	var list []sess
	for rows.Next() {
		var item sess
		var cr sql.NullString
		var closed sql.NullTime
		if err := rows.Scan(&item.ID, &item.UserID, &item.GroupID, &item.ResourceID, &item.State, &cr, &item.CreatedAt, &closed); err != nil {
			mapSvcErr(w, err)
			return
		}
		if cr.Valid {
			item.CloseReason = &cr.String
		}
		if closed.Valid {
			item.ClosedAt = &closed.Time
		}
		list = append(list, item)
	}
	if list == nil {
		list = []sess{}
	}
	writeJSON(w, http.StatusOK, map[string]any{"sessions": list})
}

func (s *Server) handleAdminListQueues(w http.ResponseWriter, r *http.Request, _ *auth.Claims) {
	rows, err := s.Store.DB.QueryContext(r.Context(), `
		SELECT id, group_id, user_id, status, seq FROM queue_entries
		WHERE status IN ('waiting','offered') ORDER BY group_id, seq`)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	defer rows.Close()
	type qe struct {
		ID, GroupID, UserID, Status string
		Seq                         int64
	}
	var list []qe
	for rows.Next() {
		var item qe
		if err := rows.Scan(&item.ID, &item.GroupID, &item.UserID, &item.Status, &item.Seq); err != nil {
			mapSvcErr(w, err)
			return
		}
		list = append(list, item)
	}
	if list == nil {
		list = []qe{}
	}
	writeJSON(w, http.StatusOK, map[string]any{"queues": list})
}

func (s *Server) handleAdminGetSettings(w http.ResponseWriter, r *http.Request, _ *auth.Claims) {
	m, err := s.Store.ListSettings(r.Context())
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"settings": m})
}

func (s *Server) handleAdminPutSettings(w http.ResponseWriter, r *http.Request, claims *auth.Claims) {
	var body map[string]string
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeErr(w, http.StatusBadRequest, "invalid json")
		return
	}
	for k, v := range body {
		if err := s.Store.UpsertSetting(r.Context(), k, v); err != nil {
			mapSvcErr(w, err)
			return
		}
	}
	_ = s.Store.InsertAudit(r.Context(), "admin", claims.UserID, "settings_update", "settings", "", "", nil)
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}

func (s *Server) handleAdminAudit(w http.ResponseWriter, r *http.Request, _ *auth.Claims) {
	rows, err := s.Store.DB.QueryContext(r.Context(), `
		SELECT id, at, actor_type, actor_id, action, COALESCE(target_type,''), COALESCE(target_id,''), COALESCE(ip,'')
		FROM audit_logs ORDER BY at DESC LIMIT 200`)
	if err != nil {
		mapSvcErr(w, err)
		return
	}
	defer rows.Close()
	type a struct {
		ID                             int64
		At                             time.Time
		ActorType, ActorID, Action     string
		TargetType, TargetID, IP       string
	}
	var list []a
	for rows.Next() {
		var item a
		if err := rows.Scan(&item.ID, &item.At, &item.ActorType, &item.ActorID, &item.Action, &item.TargetType, &item.TargetID, &item.IP); err != nil {
			mapSvcErr(w, err)
			return
		}
		list = append(list, item)
	}
	if list == nil {
		list = []a{}
	}
	writeJSON(w, http.StatusOK, map[string]any{"audit": list})
}

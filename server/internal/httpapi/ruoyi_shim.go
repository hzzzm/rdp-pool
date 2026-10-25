package httpapi

import (
	"encoding/json"
	"net/http"
	"strings"
	"time"

	"github.com/golang-jwt/jwt/v5"
)

// Minimal RuoYi-Vue3-compatible /prod-api shim so the admin UI can boot when
// the full Spring Boot ruoyi-admin image cannot be built (network/Maven).
// Architecture still targets full RuoYi Java; this is a local MVP bootstrap.
// Nginx: /prod-api/ → go-api /ruoyi/

const ruoyiShimUser = "admin"
const ruoyiShimPass = "admin123"

func (s *Server) registerRuoyiShim() {
	s.Mux.HandleFunc("/ruoyi/", s.handleRuoyiDispatch)
}

func (s *Server) handleRuoyiDispatch(w http.ResponseWriter, r *http.Request) {
	path := strings.TrimPrefix(r.URL.Path, "/ruoyi")
	path = strings.TrimSuffix(path, "/")
	if path == "" {
		path = "/"
	}
	switch {
	case r.Method == http.MethodGet && path == "/captchaImage":
		s.handleRuoyiCaptcha(w, r)
	case r.Method == http.MethodPost && path == "/login":
		s.handleRuoyiLogin(w, r)
	case r.Method == http.MethodGet && path == "/getInfo":
		s.handleRuoyiGetInfo(w, r)
	case r.Method == http.MethodGet && path == "/getRouters":
		s.handleRuoyiGetRouters(w, r)
	case r.Method == http.MethodPost && path == "/logout":
		s.handleRuoyiLogout(w, r)
	default:
		s.handleRuoyiFallback(w, r)
	}
}

func ryOK(w http.ResponseWriter, extra map[string]any) {
	m := map[string]any{"code": 200, "msg": "操作成功"}
	for k, v := range extra {
		m[k] = v
	}
	writeJSON(w, http.StatusOK, m)
}

func (s *Server) handleRuoyiCaptcha(w http.ResponseWriter, r *http.Request) {
	ryOK(w, map[string]any{
		"captchaEnabled": false,
		"uuid":           "shim",
		"img":            "",
	})
}

func (s *Server) handleRuoyiLogin(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Username string `json:"username"`
		Password string `json:"password"`
	}
	_ = json.NewDecoder(r.Body).Decode(&body)
	if body.Username == "" {
		body.Username = ruoyiShimUser
	}
	if body.Username != ruoyiShimUser || body.Password != ruoyiShimPass {
		writeJSON(w, http.StatusOK, map[string]any{"code": 500, "msg": "用户名或密码错误（shim: admin/admin123）"})
		return
	}
	tok, err := s.issueRuoyiShimToken(body.Username)
	if err != nil {
		writeJSON(w, http.StatusOK, map[string]any{"code": 500, "msg": "token error"})
		return
	}
	ryOK(w, map[string]any{"token": tok})
}

func (s *Server) issueRuoyiShimToken(login string) (string, error) {
	claims := jwt.MapClaims{
		"sub":   "ruoyi:" + login,
		"login": login,
		"kind":  "ruoyi_shim",
		"exp":   time.Now().Add(2 * time.Hour).Unix(),
		"iat":   time.Now().Unix(),
	}
	t := jwt.NewWithClaims(jwt.SigningMethodHS256, claims)
	return t.SignedString([]byte(s.Pool.Cfg.AdminJWTSecret))
}

// parseRuoyiShimToken validates a RuoYi shim JWT (kind=ruoyi_shim) signed with AdminJWTSecret.
func (s *Server) parseRuoyiShimToken(raw string) (login string, ok bool) {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return "", false
	}
	parsed, err := jwt.Parse(raw, func(t *jwt.Token) (any, error) {
		if t.Method != jwt.SigningMethodHS256 {
			return nil, jwt.ErrTokenSignatureInvalid
		}
		return []byte(s.Pool.Cfg.AdminJWTSecret), nil
	})
	if err != nil || !parsed.Valid {
		return "", false
	}
	claims, ok := parsed.Claims.(jwt.MapClaims)
	if !ok {
		return "", false
	}
	if kind, _ := claims["kind"].(string); kind != "ruoyi_shim" {
		return "", false
	}
	login, _ = claims["login"].(string)
	if login == "" {
		if sub, _ := claims["sub"].(string); strings.HasPrefix(sub, "ruoyi:") {
			login = strings.TrimPrefix(sub, "ruoyi:")
		}
	}
	if login == "" {
		return "", false
	}
	return login, true
}

func (s *Server) ruoyiBearer(r *http.Request) string {
	h := r.Header.Get("Authorization")
	if strings.HasPrefix(h, "Bearer ") {
		return strings.TrimPrefix(h, "Bearer ")
	}
	return strings.TrimSpace(h)
}

func (s *Server) ruoyiAuthed(r *http.Request) (login string, ok bool) {
	return s.parseRuoyiShimToken(s.ruoyiBearer(r))
}

func (s *Server) handleRuoyiGetInfo(w http.ResponseWriter, r *http.Request) {
	if _, ok := s.ruoyiAuthed(r); !ok {
		writeJSON(w, http.StatusOK, map[string]any{"code": 401, "msg": "未登录"})
		return
	}
	ryOK(w, map[string]any{
		"user": map[string]any{
			"userId":   1,
			"userName": "admin",
			"nickName": "管理员",
			"avatar":   "",
			"admin":    true,
		},
		"roles":              []string{"admin"},
		"permissions":        []string{"*:*:*"},
		"isDefaultModifyPwd": false,
		"isPasswordExpired":  false,
	})
}

func (s *Server) handleRuoyiGetRouters(w http.ResponseWriter, r *http.Request) {
	if _, ok := s.ruoyiAuthed(r); !ok {
		writeJSON(w, http.StatusOK, map[string]any{"code": 401, "msg": "未登录"})
		return
	}
	// Hardcoded RDP overlay routes (matches admin/overlay/ui views). Full Java
	// RuoYi would load these from sys_menu; shim keeps the pool UI reachable.
	routes := []map[string]any{
		{
			"name": "Rdp", "path": "/rdp", "hidden": false, "redirect": "noRedirect",
			"component": "Layout", "alwaysShow": true,
			"meta": map[string]any{"title": "远程桌面资源池", "icon": "monitor", "noCache": false},
			"children": []map[string]any{
				{
					"name": "RdpGroup", "path": "group", "hidden": false, "component": "rdp/group/index",
					"meta": map[string]any{"title": "资源组", "icon": "tree", "noCache": false},
				},
				{
					"name": "RdpResource", "path": "resource", "hidden": false, "component": "rdp/resource/index",
					"meta": map[string]any{"title": "Windows 资源", "icon": "server", "noCache": false},
				},
				{
					"name": "RdpUser", "path": "pool-user", "hidden": false, "component": "rdp/user/index",
					"meta": map[string]any{"title": "终端用户", "icon": "user", "noCache": false},
				},
				{
					"name": "RdpSession", "path": "session", "hidden": false, "component": "rdp/session/index",
					"meta": map[string]any{"title": "会话与排队", "icon": "log", "noCache": false},
				},
				{
					"name": "RdpSettings", "path": "settings", "hidden": false, "component": "rdp/settings/index",
					"meta": map[string]any{"title": "池配置", "icon": "edit", "noCache": false},
				},
			},
		},
	}
	ryOK(w, map[string]any{"data": routes})
}

func (s *Server) handleRuoyiLogout(w http.ResponseWriter, r *http.Request) {
	ryOK(w, nil)
}

func (s *Server) handleRuoyiFallback(w http.ResponseWriter, r *http.Request) {
	// Satisfy incidental RuoYi UI calls (dict/config/notice) with empty payloads.
	ryOK(w, map[string]any{"data": []any{}, "rows": []any{}, "total": 0})
}

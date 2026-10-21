package main

import (
	"context"
	"database/sql"
	"fmt"
	"log"
	"os"
	"time"

	_ "github.com/go-sql-driver/mysql"
	"github.com/google/uuid"
	"github.com/hzzzm/rdp-pool/server/internal/auth"
	"github.com/hzzzm/rdp-pool/server/internal/vault"
)

// Seeds demo group/user/resource from env. Real RDP password must stay in local .env only.
func main() {
	dsn := getenv("MYSQL_DSN", "rdp_app:rdp_pass@tcp(127.0.0.1:3306)/rdp_pool?parseTime=true&loc=Local&charset=utf8mb4")
	aesKey := getenv("CREDENTIAL_AES_KEY", "0123456789abcdef0123456789abcdef")
	db, err := sql.Open("mysql", dsn)
	if err != nil {
		log.Fatal(err)
	}
	defer db.Close()
	ctx := context.Background()

	for i := 0; i < 30; i++ {
		if err = db.PingContext(ctx); err == nil {
			break
		}
		time.Sleep(time.Second)
	}
	if err != nil {
		log.Fatal(err)
	}

	groupCode := getenv("DEMO_GROUP_CODE", "office")
	groupName := getenv("DEMO_GROUP_NAME", "办公桌面")
	resName := getenv("DEMO_RESOURCE_NAME", "win-test-01")
	resHost := getenv("DEMO_RESOURCE_HOST", "10.126.126.4")
	resPort := getenv("DEMO_RESOURCE_PORT", "3389")
	rdpUser := getenv("DEMO_RDP_USERNAME", "ciit")
	rdpPass := getenv("DEMO_RDP_PASSWORD", "")
	userLogin := getenv("DEMO_POOL_USER", "demo")
	userPass := getenv("DEMO_POOL_PASSWORD", "demo123")

	if rdpPass == "" || rdpPass == "CHANGE_ME" {
		log.Println("WARN: DEMO_RDP_PASSWORD empty/placeholder — resource will be seeded but RDP ticket password will be blank until you re-seed with a real local .env")
	}

	gid := "grp_" + short()
	uid := "usr_" + short()
	rid := "res_" + short()

	_, err = db.ExecContext(ctx, `
		INSERT INTO resource_groups (id, code, name, enabled, sort_order, description, created_at, updated_at)
		VALUES (?, ?, ?, 1, 0, 'demo seed', NOW(3), NOW(3))
		ON DUPLICATE KEY UPDATE name=VALUES(name), updated_at=NOW(3)`, gid, groupCode, groupName)
	_ = db.QueryRowContext(ctx, `SELECT id FROM resource_groups WHERE code=?`, groupCode).Scan(&gid)

	hash, err := auth.HashPassword(userPass)
	if err != nil {
		log.Fatal(err)
	}
	_, err = db.ExecContext(ctx, `
		INSERT INTO pool_users (id, login, password_hash, display_name, enabled, created_at, updated_at)
		VALUES (?, ?, ?, 'Demo User', 1, NOW(3), NOW(3))
		ON DUPLICATE KEY UPDATE password_hash=VALUES(password_hash), updated_at=NOW(3)`, uid, userLogin, hash)
	_ = db.QueryRowContext(ctx, `SELECT id FROM pool_users WHERE login=?`, userLogin).Scan(&uid)

	_, _ = db.ExecContext(ctx, `
		INSERT IGNORE INTO user_group_grants (user_id, group_id, created_at) VALUES (?, ?, NOW(3))`, uid, gid)

	// Upsert resource by host within group (idempotent re-seed)
	var existing string
	err = db.QueryRowContext(ctx, `SELECT id FROM resources WHERE group_id=? AND host=? LIMIT 1`, gid, resHost).Scan(&existing)
	if err == nil {
		rid = existing
		_, _ = db.ExecContext(ctx, `
			UPDATE resources SET name=?, rdp_port=?, status='idle', supply_class='clean', supply_seq=0,
			  linger_hint=0, enabled=1, current_session_id=NULL, updated_at=NOW(3)
			WHERE id=?`, resName, atoi(resPort, 3389), rid)
	} else {
		_, err = db.ExecContext(ctx, `
			INSERT INTO resources (id, group_id, name, host, rdp_port, status, supply_class, supply_seq, linger_hint, enabled, created_at, updated_at)
			VALUES (?, ?, ?, ?, ?, 'idle', 'clean', 0, 0, 1, NOW(3), NOW(3))`,
			rid, gid, resName, resHost, atoi(resPort, 3389))
		if err != nil {
			log.Fatal(err)
		}
	}

	if rdpPass != "" && rdpPass != "CHANGE_ME" {
		blob, err := vault.EncryptJSON([]byte(aesKey), vault.RDPSecret{Username: rdpUser, Password: rdpPass})
		if err != nil {
			log.Fatal(err)
		}
		_, err = db.ExecContext(ctx, `
			INSERT INTO resource_secrets (resource_id, ciphertext, key_version, updated_at)
			VALUES (?, ?, 1, NOW(3))
			ON DUPLICATE KEY UPDATE ciphertext=VALUES(ciphertext), updated_at=NOW(3)`, rid, blob)
		if err != nil {
			log.Fatal(err)
		}
	}

	fmt.Printf("seed ok: group=%s(%s) user=%s resource=%s host=%s:%s\n", groupCode, gid, userLogin, rid, resHost, resPort)
}

func getenv(k, d string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return d
}

func short() string {
	return uuid.NewString()[:8]
}

func atoi(s string, def int) int {
	var n int
	if _, err := fmt.Sscanf(s, "%d", &n); err != nil {
		return def
	}
	return n
}

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
)

// Seeds demo group/user/resource from env. Real RDP password must stay in local .env only.
func main() {
	dsn := getenv("MYSQL_DSN", "rdp_app:rdp_pass@tcp(127.0.0.1:3306)/rdp_pool?parseTime=true&loc=Local&charset=utf8mb4")
	db, err := sql.Open("mysql", dsn)
	if err != nil {
		log.Fatal(err)
	}
	defer db.Close()
	ctx := context.Background()
	if err := db.PingContext(ctx); err != nil {
		log.Fatal(err)
	}

	groupCode := getenv("DEMO_GROUP_CODE", "office")
	groupName := getenv("DEMO_GROUP_NAME", "办公桌面")
	resName := getenv("DEMO_RESOURCE_NAME", "win-test-01")
	resHost := getenv("DEMO_RESOURCE_HOST", "10.126.126.4")
	resPort := getenv("DEMO_RESOURCE_PORT", "3389")
	userLogin := getenv("DEMO_POOL_USER", "demo")
	userPass := getenv("DEMO_POOL_PASSWORD", "demo")

	gid := "grp_" + short()
	uid := "usr_" + short()
	rid := "res_" + short()

	_, err = db.ExecContext(ctx, `
		INSERT INTO resource_groups (id, code, name, enabled, sort_order, description, created_at, updated_at)
		VALUES (?, ?, ?, 1, 0, 'demo seed', NOW(3), NOW(3))
		ON DUPLICATE KEY UPDATE name=VALUES(name), updated_at=NOW(3)`, gid, groupCode, groupName)
	if err != nil {
		// if code exists, fetch id
		_ = db.QueryRowContext(ctx, `SELECT id FROM resource_groups WHERE code=?`, groupCode).Scan(&gid)
	}

	hash, err := auth.HashPassword(userPass)
	if err != nil {
		log.Fatal(err)
	}
	_, err = db.ExecContext(ctx, `
		INSERT INTO pool_users (id, login, password_hash, display_name, enabled, created_at, updated_at)
		VALUES (?, ?, ?, 'Demo User', 1, NOW(3), NOW(3))
		ON DUPLICATE KEY UPDATE password_hash=VALUES(password_hash), updated_at=NOW(3)`, uid, userLogin, hash)
	if err != nil {
		_ = db.QueryRowContext(ctx, `SELECT id FROM pool_users WHERE login=?`, userLogin).Scan(&uid)
	}

	_, _ = db.ExecContext(ctx, `
		INSERT IGNORE INTO user_group_grants (user_id, group_id, created_at) VALUES (?, ?, NOW(3))`, uid, gid)

	_, err = db.ExecContext(ctx, `
		INSERT INTO resources (id, group_id, name, host, rdp_port, status, supply_class, supply_seq, linger_hint, enabled, created_at, updated_at)
		VALUES (?, ?, ?, ?, ?, 'idle', 'clean', 0, 0, 1, NOW(3), NOW(3))`,
		rid, gid, resName, resHost, atoi(resPort, 3389))
	if err != nil {
		log.Printf("resource insert note: %v", err)
	}

	fmt.Printf("seed ok: group=%s user=%s resource_host=%s port=%s\n", groupCode, userLogin, resHost, resPort)
	fmt.Println("set DEMO_RDP_PASSWORD only in local .env; vault encryption comes next")
	_ = time.Now()
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

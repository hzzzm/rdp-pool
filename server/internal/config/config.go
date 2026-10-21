package config

import (
	"os"
	"strconv"
	"time"
)

type Config struct {
	HTTPAddr          string
	MySQLDSN          string
	RedisAddr         string
	RedisPassword     string
	JWTSecret         string
	AdminJWTSecret    string
	CredentialAESKey  string
	PendingConfirm    time.Duration
	HeartbeatInterval time.Duration
	LeaseTTL          time.Duration
	MaxOccupationTTL  time.Duration
	OfferTTL          time.Duration
	TicketTTL         time.Duration
	LingerOfferScan   time.Duration
}

func Load() Config {
	return Config{
		HTTPAddr:          getenv("HTTP_ADDR", ":8081"),
		MySQLDSN:          getenv("MYSQL_DSN", "rdp_app:rdp_pass@tcp(127.0.0.1:3306)/rdp_pool?parseTime=true&loc=Local&charset=utf8mb4"),
		RedisAddr:         getenv("REDIS_ADDR", "127.0.0.1:6379"),
		RedisPassword:     os.Getenv("REDIS_PASSWORD"),
		JWTSecret:         getenv("JWT_SECRET", "dev-pool-user-jwt"),
		AdminJWTSecret:    getenv("ADMIN_JWT_SECRET", "dev-admin-jwt"),
		CredentialAESKey:  getenv("CREDENTIAL_AES_KEY", "0123456789abcdef0123456789abcdef"),
		PendingConfirm:    durationSec("PENDING_CONFIRM_SEC", 90),
		HeartbeatInterval: durationSec("HEARTBEAT_INTERVAL_SEC", 15),
		LeaseTTL:          durationSec("LEASE_TTL_SEC", 60),
		MaxOccupationTTL:  durationSec("MAX_OCCUPATION_TTL_SEC", 14400),
		OfferTTL:          durationSec("OFFER_TTL_SEC", 60),
		TicketTTL:         durationSec("TICKET_TTL_SEC", 120),
		LingerOfferScan:   durationSec("LINGER_OFFER_SCAN_SEC", 20),
	}
}

func getenv(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}

func durationSec(k string, def int) time.Duration {
	if v := os.Getenv(k); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			return time.Duration(n) * time.Second
		}
	}
	return time.Duration(def) * time.Second
}

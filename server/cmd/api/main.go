package main

import (
	"context"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/hzzzm/rdp-pool/server/internal/auth"
	"github.com/hzzzm/rdp-pool/server/internal/config"
	"github.com/hzzzm/rdp-pool/server/internal/httpapi"
	"github.com/hzzzm/rdp-pool/server/internal/service"
	"github.com/hzzzm/rdp-pool/server/internal/store"
)

func main() {
	cfg := config.Load()

	st, err := store.OpenMySQL(cfg.MySQLDSN)
	if err != nil {
		log.Fatalf("mysql: %v", err)
	}
	defer st.Close()

	tokens := auth.NewTokenService(cfg.JWTSecret, cfg.AdminJWTSecret)
	pool := service.NewPoolService(st, tokens, cfg)
	api := httpapi.New(pool, tokens, st)

	srv := &http.Server{
		Addr:              cfg.HTTPAddr,
		Handler:           api.Handler(),
		ReadHeaderTimeout: 10 * time.Second,
	}

	go func() {
		log.Printf("rdp-pool go-api listening on %s", cfg.HTTPAddr)
		if err := srv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			log.Fatalf("listen: %v", err)
		}
	}()

	stop := make(chan os.Signal, 1)
	signal.Notify(stop, syscall.SIGINT, syscall.SIGTERM)
	<-stop

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	_ = srv.Shutdown(ctx)
	log.Println("shutdown complete")
}

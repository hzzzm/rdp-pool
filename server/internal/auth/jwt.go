package auth

import (
	"errors"
	"time"

	"github.com/golang-jwt/jwt/v5"
)

const (
	RolePoolUser = "pool_user"
	RoleAdmin    = "admin"
)

type Claims struct {
	UserID string `json:"uid"`
	Login  string `json:"login"`
	Role   string `json:"role"`
	jwt.RegisteredClaims
}

type TokenService struct {
	userSecret  []byte
	adminSecret []byte
	userTTL     time.Duration
	adminTTL    time.Duration
}

func NewTokenService(userSecret, adminSecret string) *TokenService {
	return &TokenService{
		userSecret:  []byte(userSecret),
		adminSecret: []byte(adminSecret),
		userTTL:     8 * time.Hour,
		adminTTL:    2 * time.Hour,
	}
}

func (s *TokenService) IssuePoolUser(userID, login string) (string, time.Time, error) {
	exp := time.Now().Add(s.userTTL)
	claims := Claims{
		UserID: userID,
		Login:  login,
		Role:   RolePoolUser,
		RegisteredClaims: jwt.RegisteredClaims{
			ExpiresAt: jwt.NewNumericDate(exp),
			IssuedAt:  jwt.NewNumericDate(time.Now()),
			Subject:   userID,
		},
	}
	t := jwt.NewWithClaims(jwt.SigningMethodHS256, claims)
	signed, err := t.SignedString(s.userSecret)
	return signed, exp, err
}

func (s *TokenService) IssueAdmin(adminID, login string) (string, time.Time, error) {
	exp := time.Now().Add(s.adminTTL)
	claims := Claims{
		UserID: adminID,
		Login:  login,
		Role:   RoleAdmin,
		RegisteredClaims: jwt.RegisteredClaims{
			ExpiresAt: jwt.NewNumericDate(exp),
			IssuedAt:  jwt.NewNumericDate(time.Now()),
			Subject:   adminID,
		},
	}
	t := jwt.NewWithClaims(jwt.SigningMethodHS256, claims)
	signed, err := t.SignedString(s.adminSecret)
	return signed, exp, err
}

func (s *TokenService) ParsePoolUser(token string) (*Claims, error) {
	return s.parse(token, s.userSecret, RolePoolUser)
}

func (s *TokenService) ParseAdmin(token string) (*Claims, error) {
	return s.parse(token, s.adminSecret, RoleAdmin)
}

func (s *TokenService) parse(token string, secret []byte, expectRole string) (*Claims, error) {
	parsed, err := jwt.ParseWithClaims(token, &Claims{}, func(t *jwt.Token) (any, error) {
		if t.Method != jwt.SigningMethodHS256 {
			return nil, errors.New("unexpected signing method")
		}
		return secret, nil
	})
	if err != nil {
		return nil, err
	}
	claims, ok := parsed.Claims.(*Claims)
	if !ok || !parsed.Valid {
		return nil, errors.New("invalid token")
	}
	if claims.Role != expectRole {
		return nil, errors.New("role mismatch")
	}
	return claims, nil
}

package auth

import "golang.org/x/crypto/bcrypt"

// HashPassword uses bcrypt for MVP scaffolding.
// Architecture recommends Argon2id for production; swap in a follow-up.
func HashPassword(plain string) (string, error) {
	b, err := bcrypt.GenerateFromPassword([]byte(plain), bcrypt.DefaultCost)
	return string(b), err
}

func CheckPassword(hash, plain string) bool {
	return bcrypt.CompareHashAndPassword([]byte(hash), []byte(plain)) == nil
}

package vault

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"encoding/json"
	"errors"
	"fmt"
	"io"
)

type RDPSecret struct {
	Username string `json:"username"`
	Password string `json:"password"`
}

// EncryptJSON AES-GCM with 16/24/32-byte key. Output: nonce||ciphertext.
func EncryptJSON(key []byte, v any) ([]byte, error) {
	if len(key) != 16 && len(key) != 24 && len(key) != 32 {
		return nil, fmt.Errorf("aes key must be 16/24/32 bytes, got %d", len(key))
	}
	plain, err := json.Marshal(v)
	if err != nil {
		return nil, err
	}
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}
	nonce := make([]byte, gcm.NonceSize())
	if _, err := io.ReadFull(rand.Reader, nonce); err != nil {
		return nil, err
	}
	return gcm.Seal(nonce, nonce, plain, nil), nil
}

func DecryptRDPSecret(key, blob []byte) (*RDPSecret, error) {
	if len(key) != 16 && len(key) != 24 && len(key) != 32 {
		return nil, errors.New("invalid aes key length")
	}
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}
	if len(blob) < gcm.NonceSize() {
		return nil, errors.New("ciphertext too short")
	}
	nonce, ct := blob[:gcm.NonceSize()], blob[gcm.NonceSize():]
	plain, err := gcm.Open(nil, nonce, ct, nil)
	if err != nil {
		return nil, err
	}
	var s RDPSecret
	if err := json.Unmarshal(plain, &s); err != nil {
		return nil, err
	}
	return &s, nil
}

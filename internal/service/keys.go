package service

import (
	"crypto/rand"
	"fmt"

	"wg-agent/internal/platform"
)

// GenerateKey returns a WireGuard private key from a cryptographically secure
// source — REQ-KEY-001 for an interface, REQ-KEY-011 for a peer.
//
// The clamping is Curve25519's. The kernel applies it on receipt in any case,
// and applying it here means the value stored is the value in effect, so a
// public key derived from the stored private key matches the one the device
// reports back.
func GenerateKey() (platform.Key, error) {
	var b [32]byte
	if _, err := rand.Read(b[:]); err != nil {
		return platform.Key{}, fmt.Errorf("read random bytes: %w", err)
	}
	b[0] &= 248
	b[31] &= 127
	b[31] |= 64
	return platform.KeyFromBytes(b[:]), nil
}

// PublicKeyOf derives the public key from a private one, so a caller that
// supplied its own key learns what to configure without a round trip to the
// kernel. platform.Key owns the derivation, so the fake and the kernel agree.
func PublicKeyOf(priv platform.Key) (string, error) {
	pub, err := priv.PublicKey()
	if err != nil {
		return "", err
	}
	return pub.Base64(), nil
}

// KeyPair is what REQ-KEY-040 returns: one private key and its public key, in
// that single response. REQ-KEY-013 forbids any API that returns an already
// generated private key, so this is the only moment the value leaves the agent.
type KeyPair struct {
	PrivateKey string `json:"private_key"`
	PublicKey  string `json:"public_key"`
}

// GenerateKeyPair implements REQ-KEY-040.
//
// REQ-KEY-012 keeps the private key out of the store, the log and the audit
// log. Nothing here writes it anywhere, so the caller is its only holder once
// the response is sent.
func GenerateKeyPair() (*KeyPair, error) {
	priv, err := GenerateKey()
	if err != nil {
		return nil, err
	}
	pub, err := PublicKeyOf(priv)
	if err != nil {
		return nil, err
	}
	return &KeyPair{PrivateKey: priv.Base64(), PublicKey: pub}, nil
}

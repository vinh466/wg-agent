package service

import (
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"sort"

	"wg-agent/internal/model"
)

// Revision computes the opaque value of REQ-API-030 and REQ-RES-031.
//
// It covers the peer set as well as the spec, which is REQ-API-077: peers are a
// separate collection under REQ-RES-002, so a revision bound to the spec alone
// would not move when a peer was added.
//
// The value is a hash rather than a counter, so it is derivable from state
// alone. A counter would be another thing the store has to hold and keep
// consistent with what it describes, and REQ-RES-031 says the caller compares
// the value without interpreting it — which a hash satisfies and a counter
// only appears to improve on.
//
// The optimistic concurrency of REQ-API-031 to REQ-API-033 is deferred under
// B-05. The value is produced regardless, because REQ-RES-031 puts it in
// `status` whether or not anything enforces it yet.
func Revision(spec model.InterfaceSpec, peers []model.Peer) string {
	// Peers are sorted so the value depends on the set rather than on the order
	// the store happened to write it in.
	sorted := append([]model.Peer(nil), peers...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].PublicKey < sorted[j].PublicKey })

	h := sha256.New()
	enc := json.NewEncoder(h)
	if err := enc.Encode(spec); err != nil {
		// json.Encoder writing to a hash cannot fail on these types, and a
		// silent empty revision would be worse than a visible one.
		return "revision-error"
	}
	for _, p := range sorted {
		if err := enc.Encode(p); err != nil {
			return "revision-error"
		}
	}
	sum := h.Sum(nil)
	// Truncated to 16 bytes: enough that two states colliding is not a concern,
	// short enough to sit in an ETag header without dominating it.
	return base64.RawURLEncoding.EncodeToString(sum[:16])
}

// PeerRevision is the same value for one peer. SPEC-01 section 4.3 gives a peer
// its own `revision`, referred to REQ-RES-031 like the interface's, and a
// peer's spec is the whole of what it describes.
func PeerRevision(p model.Peer) string {
	h := sha256.New()
	if err := json.NewEncoder(h).Encode(p); err != nil {
		return "revision-error"
	}
	sum := h.Sum(nil)
	return base64.RawURLEncoding.EncodeToString(sum[:16])
}

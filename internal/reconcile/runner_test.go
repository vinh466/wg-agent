package reconcile

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"math/rand"
	"sync/atomic"
	"testing"
	"time"

	"wg-agent/internal/model"
	"wg-agent/internal/platform"
	"wg-agent/internal/platform/fake"
)

// countingStore is desired state that records how often a pass read it, which
// is how a test counts passes without reaching into the engine.
type countingStore struct {
	passes atomic.Int64
	fail   atomic.Bool
}

func (c *countingStore) Names() []string {
	c.passes.Add(1)
	if c.fail.Load() {
		return []string{"wgx"}
	}
	return nil
}

func (c *countingStore) Interface(string) (model.InterfaceSpec, bool, error) {
	// A spec with an unparseable address, so the pass fails on demand.
	return model.InterfaceSpec{Addresses: []string{"not-a-prefix"}, Enabled: true}, true, nil
}
func (c *countingStore) Peers(string) ([]model.Peer, error) { return nil, nil }
func (c *countingStore) Describes(string) bool              { return c.fail.Load() }
func (c *countingStore) DeletionRecord(string) bool         { return false }
func (c *countingStore) DeletionNames() []string            { return nil }
func (c *countingStore) ClearDeletion(string) error         { return nil }

func quietLog() *slog.Logger {
	return slog.New(slog.NewTextHandler(io.Discard, &slog.HandlerOptions{Level: slog.LevelError}))
}

func runnerOver(c *countingStore, n *fake.Node) *Runner {
	return &Runner{
		Engine:     &Engine{Store: c, Link: fake.LinkView{N: n}, Device: n},
		Log:        quietLog(),
		Interval:   time.Hour, // the timer must not fire unless a test wants it
		BackoffMin: time.Millisecond,
		BackoffMax: 4 * time.Millisecond,
	}
}

// waitFor polls until cond holds or the deadline passes. Polling rather than
// sleeping a fixed time keeps the test quick when it passes and honest when it
// does not.
func waitFor(t *testing.T, d time.Duration, cond func() bool) bool {
	t.Helper()
	deadline := time.Now().Add(d)
	for time.Now().Before(deadline) {
		if cond() {
			return true
		}
		time.Sleep(time.Millisecond)
	}
	return cond()
}

// REQ-RCN-020 lists agent startup among the triggers, so a pass happens before
// the first interval elapses.
func TestRunner_ReconcilesOnStartup_REQ_RCN_020(t *testing.T) {
	c := &countingStore{}
	r := runnerOver(c, fake.NewNode())

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go func() { _ = r.Run(ctx) }()

	if !waitFor(t, time.Second, func() bool { return c.passes.Load() >= 1 }) {
		t.Fatal("no pass ran at startup")
	}
}

// REQ-RCN-020 lists the periodic timer, default 30 s.
func TestRunner_ReconcilesOnTimer_REQ_RCN_020(t *testing.T) {
	c := &countingStore{}
	r := runnerOver(c, fake.NewNode())
	r.Interval = 2 * time.Millisecond

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go func() { _ = r.Run(ctx) }()

	if !waitFor(t, 2*time.Second, func() bool { return c.passes.Load() >= 4 }) {
		t.Fatalf("timer produced %d passes, want at least 4", c.passes.Load())
	}
}

// An API write triggers a pass on the affected interface — REQ-RCN-020.
func TestRunner_ReconcilesOnTrigger_REQ_RCN_020(t *testing.T) {
	c := &countingStore{}
	r := runnerOver(c, fake.NewNode())

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go func() { _ = r.Run(ctx) }()

	if !waitFor(t, time.Second, func() bool { return c.passes.Load() >= 1 }) {
		t.Fatal("startup pass did not run")
	}
	start := c.passes.Load()
	r.Trigger("wg0")

	if !waitFor(t, time.Second, func() bool { return c.passes.Load() > start }) {
		t.Fatal("a trigger did not produce a pass")
	}
}

// REQ-RCN-021: a netlink event reporting a deleted link is what detects an
// externally removed interface, rather than waiting out the timer.
func TestRunner_ReconcilesOnLinkEvent_REQ_RCN_021(t *testing.T) {
	c := &countingStore{}
	ev := fake.NewEvents()
	r := runnerOver(c, fake.NewNode())
	r.Events = ev

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go func() { _ = r.Run(ctx) }()

	if !waitFor(t, time.Second, func() bool { return c.passes.Load() >= 1 }) {
		t.Fatal("startup pass did not run")
	}
	start := c.passes.Load()
	ev.Send("wg0", true)

	if !waitFor(t, time.Second, func() bool { return c.passes.Load() > start }) {
		t.Fatal("a link event did not produce a pass")
	}
}

// A subscription that cannot be established leaves the timer as the only
// trigger. Reconciliation still converges; stopping the agent would be worse.
func TestRunner_SurvivesAnUnavailableSubscription_REQ_RCN_021(t *testing.T) {
	c := &countingStore{}
	r := runnerOver(c, fake.NewNode())
	r.Events = &fake.Events{Err: errors.New("no netlink here")}
	r.Interval = 2 * time.Millisecond

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go func() { _ = r.Run(ctx) }()

	if !waitFor(t, 2*time.Second, func() bool { return c.passes.Load() >= 3 }) {
		t.Fatalf("passes = %d; the timer must carry on without a subscription",
			c.passes.Load())
	}
}

// REQ-RCN-041: the retry backoff is exponential, jittered, and stays between
// backoff_min and backoff_max.
func TestRunner_BackoffStaysWithinBounds_REQ_RCN_041(t *testing.T) {
	r := &Runner{
		BackoffMin: time.Second,
		BackoffMax: 16 * time.Second,
		Rand:       rand.New(rand.NewSource(1)),
	}

	// Growth doubles and clamps.
	b := r.backoffMin()
	want := []time.Duration{2, 4, 8, 16, 16, 16}
	for i, w := range want {
		b = r.grow(b)
		if b != w*time.Second {
			t.Fatalf("step %d: backoff = %v, want %v", i, b, w*time.Second)
		}
	}

	// Jitter never leaves the configured window, at any point of the curve.
	for _, backoff := range []time.Duration{
		time.Second, 2 * time.Second, 8 * time.Second, 16 * time.Second,
	} {
		for i := 0; i < 500; i++ {
			d := r.jitter(backoff)
			if d < r.backoffMin() || d > r.backoffMax() {
				t.Fatalf("jitter(%v) = %v, outside [%v, %v]",
					backoff, d, r.backoffMin(), r.backoffMax())
			}
			if d > backoff {
				t.Fatalf("jitter(%v) = %v, above the current backoff", backoff, d)
			}
		}
	}
}

// A failing pass retries on the backoff rather than the interval, so recovery
// does not wait out a 30-second timer.
func TestRunner_RetriesFasterThanTheIntervalOnFailure_REQ_RCN_041(t *testing.T) {
	c := &countingStore{}
	c.fail.Store(true)
	r := runnerOver(c, fake.NewNode())
	r.Interval = time.Hour

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go func() { _ = r.Run(ctx) }()

	// With the interval at an hour, more than one pass can only come from the
	// backoff path.
	if !waitFor(t, 2*time.Second, func() bool { return c.passes.Load() >= 3 }) {
		t.Fatalf("passes = %d; a failing pass must retry on the backoff",
			c.passes.Load())
	}
}

// REQ-API-073 and REQ-API-074: the loop stops on cancellation and alters no
// interface on the way out.
func TestRunner_ShutdownLeavesTheKernelAlone_REQ_API_074(t *testing.T) {
	n := fake.NewNode()
	n.AddInterface("wg0", "10.100.0.1/24", 51820)
	c := &countingStore{}
	r := runnerOver(c, n)

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- r.Run(ctx) }()

	if !waitFor(t, time.Second, func() bool { return c.passes.Load() >= 1 }) {
		t.Fatal("startup pass did not run")
	}
	n.ResetCalls()
	cancel()

	select {
	case err := <-done:
		if !errors.Is(err, context.Canceled) {
			t.Errorf("Run returned %v, want context.Canceled", err)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("Run did not return after cancellation")
	}

	if len(n.Calls) != 0 {
		t.Errorf("shutdown wrote to the kernel: %v", n.Calls)
	}
	if _, ok := n.Links["wg0"]; !ok {
		t.Error("shutdown removed an interface")
	}
}

// A trigger that arrives before Run starts is not dropped. The service layer
// holds the runner before the loop is running, so an API write during startup
// still gets its pass — REQ-RCN-020.
func TestRunner_TriggerBeforeRunIsNotLost_REQ_RCN_020(t *testing.T) {
	c := &countingStore{}
	r := runnerOver(c, fake.NewNode())
	r.Interval = time.Hour

	r.Trigger("wg0")

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go func() { _ = r.Run(ctx) }()

	// The startup pass is one; the queued trigger is the second. With the
	// interval at an hour, nothing else can produce it.
	if !waitFor(t, 2*time.Second, func() bool { return c.passes.Load() >= 2 }) {
		t.Fatalf("passes = %d, want 2: a trigger before Run must survive",
			c.passes.Load())
	}
}

// A burst of events produces one pass, not one per event: a pass covers every
// interface in any case, so N passes would be N times the work for the same
// result — REQ-RCN-021.
func TestRunner_CoalescesAnEventBurst_REQ_RCN_021(t *testing.T) {
	ch := make(chan platform.LinkEvent, 8)
	for i := 0; i < 5; i++ {
		ch <- platform.LinkEvent{Name: "wg0", Deleted: true}
	}

	out := drain(ch, quietLog())
	if out == nil {
		t.Fatal("drain closed a channel that is open")
	}
	if len(ch) != 0 {
		t.Errorf("%d events left queued, want 0", len(ch))
	}

	// A closed subscription is reported as nil, so the loop stops selecting
	// on it rather than spinning on a closed channel.
	closed := make(chan platform.LinkEvent)
	close(closed)
	if drain(closed, quietLog()) != nil {
		t.Error("drain must report a closed subscription as nil")
	}
}

// Compile-time proof that the fake subscription satisfies the port.
var _ platform.LinkEvents = (*fake.Events)(nil)

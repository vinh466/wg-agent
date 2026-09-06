package reconcile

import (
	"context"
	"log/slog"
	"math/rand"
	"time"

	"wg-agent/internal/platform"
)

// Defaults matching the configuration sample of SPEC-09.
const (
	DefaultInterval   = 30 * time.Second
	DefaultBackoffMin = 1 * time.Second
	DefaultBackoffMax = 60 * time.Second
)

// Runner drives the engine on the triggers of REQ-RCN-020.
//
// It covers three of the five: startup, the periodic timer and the netlink
// events of REQ-RCN-021. The other two — an API write and an explicit Reconcile
// RPC — reach the engine through Trigger, which the service layer calls.
type Runner struct {
	Engine *Engine
	// Events is the subscription of REQ-RCN-021. A nil value leaves the timer
	// as the only source of drift detection, which is what the requirement
	// exists to rule out; it is nil-able so a test can drive the loop directly.
	Events platform.LinkEvents
	Log    *slog.Logger

	Interval   time.Duration
	BackoffMin time.Duration
	BackoffMax time.Duration

	// Rand makes the jitter of REQ-RCN-041 reproducible in a test. A nil value
	// uses the global source.
	Rand *rand.Rand

	trigger chan string
}

// Trigger asks for a pass. The name is advisory: a pass covers every interface,
// because REQ-RCN-036 has to classify the whole host in any case. A full
// channel is not an error — a pass is already pending, which is what the
// caller wanted.
func (r *Runner) Trigger(name string) {
	if r.trigger == nil {
		return
	}
	select {
	case r.trigger <- name:
	default:
	}
}

func (r *Runner) log() *slog.Logger {
	if r.Log != nil {
		return r.Log
	}
	return slog.Default()
}

func (r *Runner) interval() time.Duration {
	if r.Interval > 0 {
		return r.Interval
	}
	return DefaultInterval
}

func (r *Runner) backoffMin() time.Duration {
	if r.BackoffMin > 0 {
		return r.BackoffMin
	}
	return DefaultBackoffMin
}

func (r *Runner) backoffMax() time.Duration {
	if r.BackoffMax > 0 {
		return r.BackoffMax
	}
	return DefaultBackoffMax
}

// jitter returns a delay in [backoff_min, backoff] — REQ-RCN-041.
//
// The lower bound is backoff_min rather than zero, because the requirement puts
// the retry between the two configured values. The upper bound is where the
// exponential has reached, so early retries stay quick and a persistent failure
// settles at backoff_max instead of spinning.
func (r *Runner) jitter(backoff time.Duration) time.Duration {
	lo := r.backoffMin()
	if backoff <= lo {
		return lo
	}
	span := int64(backoff - lo)
	var n int64
	if r.Rand != nil {
		n = r.Rand.Int63n(span)
	} else {
		n = rand.Int63n(span)
	}
	return lo + time.Duration(n)
}

// Run reconciles until ctx is cancelled.
//
// The startup pass of REQ-RCN-020 happens before the loop, so an agent that has
// just restarted brings the host back to desired state without waiting out an
// interval. Cancellation returns ctx.Err() and writes nothing to the kernel,
// which is what REQ-API-074 requires of shutdown.
func (r *Runner) Run(ctx context.Context) error {
	r.trigger = make(chan string, 1)

	done := make(chan struct{})
	defer close(done)

	var events <-chan platform.LinkEvent
	if r.Events != nil {
		ch, err := r.Events.Subscribe(done)
		if err != nil {
			// A missing subscription degrades the agent to the timer alone
			// rather than stopping it. Reconciliation still converges; it
			// converges more slowly, which is worth a warning and not an exit.
			r.log().Warn("link event subscription unavailable; "+
				"reconciliation falls back to the periodic timer",
				"error", err)
		} else {
			events = ch
		}
	}

	backoff := r.backoffMin()
	delay := r.interval()

	// Startup pass — REQ-RCN-020.
	if err := r.pass(); err != nil {
		delay = r.jitter(backoff)
		backoff = r.grow(backoff)
	}

	timer := time.NewTimer(delay)
	defer timer.Stop()

	for {
		select {
		case <-ctx.Done():
			return ctx.Err()

		case <-timer.C:
		case <-r.trigger:
		case ev, ok := <-events:
			if !ok {
				// The subscription ended. Carry on with the timer rather than
				// stopping: the data plane does not depend on the agent.
				events = nil
				continue
			}
			r.log().Info("link event", "interface", ev.Name, "deleted", ev.Deleted)
		}

		if !timer.Stop() {
			select {
			case <-timer.C:
			default:
			}
		}

		if err := r.pass(); err != nil {
			delay = r.jitter(backoff)
			backoff = r.grow(backoff)
		} else {
			backoff = r.backoffMin()
			delay = r.interval()
		}
		timer.Reset(delay)
	}
}

func (r *Runner) grow(backoff time.Duration) time.Duration {
	next := backoff * 2
	if next > r.backoffMax() {
		return r.backoffMax()
	}
	return next
}

// pass runs one reconcile pass and logs the outcome. The error is returned so
// the caller can drive the backoff; the desired state is retained either way,
// which is the first half of REQ-RCN-040.
func (r *Runner) pass() error {
	err := r.Engine.Pass()
	if err != nil {
		r.log().Warn("reconcile pass incomplete", "error", err)
	}
	return err
}

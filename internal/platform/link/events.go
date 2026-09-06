package link

import (
	"fmt"
	"syscall"

	"github.com/vishvananda/netlink"

	"wg-agent/internal/platform"
)

// Subscribe implements platform.LinkEvents against the kernel's route netlink
// socket — REQ-RCN-021.
//
// Only two kinds of change are forwarded: a link deleted, and a link whose
// administrative flag went down. Everything else the kernel reports on this
// socket, address changes included, is already covered by the periodic pass of
// REQ-RCN-020, and forwarding it would make every unrelated interface on the
// host wake the agent.
func (n *Netlink) Subscribe(done <-chan struct{}) (<-chan platform.LinkEvent, error) {
	updates := make(chan netlink.LinkUpdate, 64)
	if err := netlink.LinkSubscribeWithOptions(updates, done, netlink.LinkSubscribeOptions{
		// ListExisting would replay the whole interface list as events. The
		// startup pass of REQ-RCN-020 already covers that ground.
		ErrorCallback: func(error) {},
	}); err != nil {
		return nil, fmt.Errorf("subscribe to link events: %w", err)
	}

	out := make(chan platform.LinkEvent, 64)
	go func() {
		defer close(out)
		for u := range updates {
			ev, interesting := classify(u)
			if !interesting {
				continue
			}
			select {
			case out <- ev:
			case <-done:
				return
			}
		}
	}()
	return out, nil
}

func classify(u netlink.LinkUpdate) (platform.LinkEvent, bool) {
	name := u.Attrs().Name
	if name == "" {
		return platform.LinkEvent{}, false
	}
	switch u.Header.Type {
	case syscall.RTM_DELLINK:
		return platform.LinkEvent{Name: name, Deleted: true}, true
	case syscall.RTM_NEWLINK:
		if u.Attrs().Flags&syscall.IFF_UP == 0 {
			return platform.LinkEvent{Name: name}, true
		}
	}
	return platform.LinkEvent{}, false
}

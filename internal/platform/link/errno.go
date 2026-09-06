package link

import (
	"errors"
	"syscall"

	"github.com/vishvananda/netlink"
)

// The netlink library returns bare syscall errnos. Naming them here keeps the
// call sites readable and the tolerance deliberate: a reconcile pass that adds
// an address already present, or removes one already gone, has reached the end
// state it was asked for.
var (
	unixEEXIST = syscall.EEXIST
	unixENOENT = syscall.ENOENT
	unixESRCH  = syscall.ESRCH
)

// isNotFound reports the several ways the kernel says "no such thing".
// LinkByName returns a typed error for a missing link, while address and route
// removal return ENOENT or ESRCH.
func isNotFound(err error) bool {
	if err == nil {
		return false
	}
	var notFound netlink.LinkNotFoundError
	if errors.As(err, &notFound) {
		return true
	}
	return errors.Is(err, unixENOENT) || errors.Is(err, unixESRCH)
}

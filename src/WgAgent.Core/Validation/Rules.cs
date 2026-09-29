using System.Net;
using System.Text.RegularExpressions;
using WgAgent.Core.Model;
using WgAgent.Platform;

namespace WgAgent.Core.Validation;

public sealed partial class Validator
{
    // REQ-VAL-010. The 15-character ceiling is IFNAMSIZ less the terminator, so
    // a name this rejects is one the kernel would truncate.
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9_-]{0,14}$")]
    private static partial Regex NamePattern();

    // MTU bounds of REQ-VAL-032, a warning rather than an error: the values
    // outside are unusual rather than impossible.
    private const int MtuMin = 1280;
    private const int MtuMax = 1500;

    // ── errors ────────────────────────────────────────────────────────────────

    // REQ-VAL-010.
    private static void CheckName(ValidationResult r, string name)
    {
        if (!NamePattern().IsMatch(name))
            r.Errf(ReasonCodes.NameInvalid, "name",
                $"\"{name}\" does not match ^[a-zA-Z][a-zA-Z0-9_-]{{0,14}}$");
    }

    // REQ-VAL-016 and REQ-VAL-020 for the interface addresses, and REQ-VAL-014.
    private void CheckAddresses(ValidationResult r, string name, InterfaceSpec spec)
    {
        if (spec.Addresses.Count == 0)
        {
            // REQ-VAL-016 — REQ-KEY-031 would have no address to substitute when
            // generating a client configuration.
            r.Errf(ReasonCodes.AddressesRequired, "addresses",
                "an interface must carry at least one address");
            return;
        }

        var mine = new List<Cidr>(spec.Addresses.Count);
        foreach (string s in spec.Addresses)
        {
            if (!Cidr.TryParse(s, out var p, out string? err))
            {
                r.Errf(ReasonCodes.AddressConflict, "addresses", $"\"{s}\" is not a CIDR: {err}");
                continue;
            }
            // REQ-VAL-020 — ADR-0005 confines v1 to IPv4, and silent omission is
            // prohibited as much as accepting the value without configuring it.
            if (IsIPv6(p))
            {
                r.Errf(ReasonCodes.IPv6NotSupported, "addresses",
                    $"\"{s}\" is IPv6; this version handles IPv4 only");
                continue;
            }
            mine.Add(p);
        }

        // REQ-VAL-014 — a subnet held by a link the agent did not create collides
        // just as firmly, so foreign interfaces are included. The interface the
        // spec names is excluded, so validating an adopted spec does not match it
        // against the link it was read from.
        foreach (var p in mine)
        {
            foreach (var (other, addrs) in _host.Addresses)
            {
                if (other == name) continue;
                foreach (var q in addrs)
                {
                    if (p.Overlaps(q))
                        r.Errf(ReasonCodes.AddressConflict, "addresses",
                            $"{p} overlaps {q} on {other}");
                }
            }
        }
    }

    // REQ-VAL-013.
    private void CheckListenPort(ValidationResult r, string name, InterfaceSpec spec)
    {
        if (spec.ListenPort == 0) return;
        foreach (var (other, port) in _host.Ports)
        {
            if (other == name || port != spec.ListenPort) continue;
            // Two WireGuard devices may hold one port while at most one is up; the
            // second bind is refused on the transition to up. Rejecting here is
            // what turns that into a message at write time.
            r.Errf(ReasonCodes.ListenPortInUse, "listen_port",
                $"port {spec.ListenPort} is held by {other}");
        }
    }

    // REQ-VAL-015 — a create naming an existing WireGuard link, unless a deletion
    // record names it.
    private void CheckCreateCollision(ValidationResult r, string name, Op op)
    {
        if (op != Op.Create) return;
        if (!_host.Ports.ContainsKey(name)) return;
        // The exemption covers the agent's own orphan under REQ-RCN-034: refusing
        // to recreate a link it failed to delete would leave a shell on the node
        // as the only recovery.
        if (_desired is not null && _desired.DeletionRecord(name)) return;
        r.Errf(ReasonCodes.InterfaceExists, "name",
            $"a WireGuard link named {name} already exists; adopt it with `wg-agent adopt {name}`");
    }

    // REQ-VAL-021.
    private static void CheckForwardPolicy(ValidationResult r, InterfaceSpec spec)
    {
        if (spec.ForwardPolicy.External == Axis.Allow && !spec.Nat.EnableUplinkForwarding)
            r.Errf(ReasonCodes.NeedsUplink, "forward_policy.external",
                "external = ALLOW requires nat.enable_uplink_forwarding");
    }

    // REQ-VAL-022 — an entry naming an interface desired state does not describe.
    //
    // Membership of desired state is the test rather than presence on the host:
    // the inter-interface policy of REQ-FWD-017 is a relationship between two
    // interfaces the agent manages, and a foreign link is not one of them.
    private void CheckPeerInterfaces(ValidationResult r, InterfaceSpec spec)
    {
        if (spec.ForwardPolicy.AllowedPeerInterfaces.Count == 0) return;
        var known = new HashSet<string>(SortedNames());
        foreach (string n in spec.ForwardPolicy.AllowedPeerInterfaces)
        {
            if (!known.Contains(n))
                r.Errf(ReasonCodes.PeerInterfaceNotFound, "forward_policy.allowed_peer_interfaces",
                    $"\"{n}\" is not an interface desired state describes");
        }
    }

    // REQ-VAL-011, REQ-VAL-017 and REQ-VAL-020 for one peer.
    private static void CheckPeerErrors(ValidationResult r, Peer p)
    {
        string field = "peers[" + p.PublicKey + "]";

        // REQ-VAL-011 — base64 of exactly 32 bytes.
        if (!Key.TryParse(p.PublicKey, out _, out string? keyErr))
            r.Errf(ReasonCodes.PublicKeyInvalid, field + ".public_key",
                $"\"{p.PublicKey}\" is not base64 of 32 bytes: {keyErr}");
        if (p.Spec.PresharedKey.Length != 0)
        {
            if (!Key.TryParse(p.Spec.PresharedKey, out _, out string? pskErr))
                r.Errf(ReasonCodes.PublicKeyInvalid, field + ".preshared_key",
                    $"the preshared key is not base64 of 32 bytes: {pskErr}");
        }

        // REQ-VAL-017 — a peer with no allowed_ips receives no traffic, so
        // storing it would describe an interface the operator did not mean.
        if (p.Spec.AllowedIPs.Count == 0)
        {
            r.Errf(ReasonCodes.AllowedIPsRequired, field + ".allowed_ips",
                "a peer must carry at least one allowed_ips entry");
            return;
        }
        foreach (string s in p.Spec.AllowedIPs)
        {
            if (!Cidr.TryParse(s, out var q, out string? err))
            {
                r.Errf(ReasonCodes.AllowedIPsDuplicate, field + ".allowed_ips",
                    $"\"{s}\" is not a CIDR: {err}");
                continue;
            }
            if (IsIPv6(q))
                r.Errf(ReasonCodes.IPv6NotSupported, field + ".allowed_ips",
                    $"\"{s}\" is IPv6; this version handles IPv4 only");
        }
    }

    // REQ-VAL-012 — two peers holding an identical entry. Cryptokey routing would
    // be ambiguous: for two identical prefixes the later-configured peer silently
    // displaces the earlier one.
    private static void CheckDuplicateAllowedIPs(ValidationResult r, IReadOnlyList<Peer> peers)
    {
        var owner = new Dictionary<Cidr, string>();
        foreach (var p in peers)
        {
            foreach (string s in p.Spec.AllowedIPs)
            {
                if (!Cidr.TryParse(s, out var q, out _)) continue;
                q = q.Masked();
                if (owner.TryGetValue(q, out string? first) && first != p.PublicKey)
                {
                    r.Errf(ReasonCodes.AllowedIPsDuplicate, "peers.allowed_ips",
                        $"{q} is claimed by both {first} and {p.PublicKey}");
                    continue;
                }
                owner[q] = p.PublicKey;
            }
        }
    }

    // ── warnings ────────────────────────────────────────────────────────────────

    // REQ-VAL-032.
    private static void CheckMtu(ValidationResult r, InterfaceSpec spec)
    {
        if (spec.Mtu == 0) return;
        if (spec.Mtu < MtuMin || spec.Mtu > MtuMax)
            r.Warnf(ReasonCodes.MtuOutOfRange, "mtu",
                $"{spec.Mtu} falls outside the usual range {MtuMin} to {MtuMax}");
    }

    // REQ-VAL-023 — an ALLOW_LIST relationship declared in one direction only.
    // Valid and stateful under REQ-FWD-017, but usually a forgotten reciprocal.
    private void CheckOneSidedAllowList(ValidationResult r, string name, InterfaceSpec spec)
    {
        if (spec.ForwardPolicy.InterInterface != Axis.AllowList || _desired is null) return;
        foreach (string other in spec.ForwardPolicy.AllowedPeerInterfaces)
        {
            if (!_desired.TryGetInterface(other, out var os)) continue; // REQ-VAL-022 reports the unknown name.
            bool reciprocal = false;
            foreach (string back in os.ForwardPolicy.AllowedPeerInterfaces)
            {
                if (back == name) { reciprocal = true; break; }
            }
            if (!reciprocal)
                r.Warnf(ReasonCodes.OneSided, "forward_policy.allowed_peer_interfaces",
                    $"{name} permits {other}, and {other} does not permit {name} in return");
        }
    }

    // REQ-VAL-034 — the field is ignored in that combination.
    private static void CheckIgnoredPeerInterfaces(ValidationResult r, InterfaceSpec spec)
    {
        if (spec.ForwardPolicy.AllowedPeerInterfaces.Count == 0) return;
        if (spec.ForwardPolicy.InterInterface != Axis.AllowList)
            r.Warnf(ReasonCodes.PeerInterfacesIgnored, "forward_policy.allowed_peer_interfaces",
                $"the list is ignored while inter_interface is {AxisWire(spec.ForwardPolicy.InterInterface)}");
    }

    // REQ-VAL-035 — traffic leaves without source NAT, so return traffic almost
    // certainly has no route back.
    private static void CheckExternalWithoutNat(ValidationResult r, InterfaceSpec spec)
    {
        if (spec.ForwardPolicy.External == Axis.Allow && !spec.Nat.Enabled)
            r.Warnf(ReasonCodes.ExternalWithoutNat, "nat.enabled",
                "external = ALLOW without source NAT; return traffic needs a route back");
    }

    // REQ-VAL-030 — entries that overlap at differing prefix lengths. Longest-
    // prefix matching makes this valid, and it usually indicates a mistake.
    private static void CheckOverlappingAllowedIPs(ValidationResult r, IReadOnlyList<Peer> peers)
    {
        var all = new List<(Cidr Prefix, string Peer)>();
        foreach (var p in peers)
        {
            foreach (string s in p.Spec.AllowedIPs)
            {
                if (Cidr.TryParse(s, out var q, out _)) all.Add((q.Masked(), p.PublicKey));
            }
        }
        for (int i = 0; i < all.Count; i++)
        {
            for (int j = i + 1; j < all.Count; j++)
            {
                var a = all[i];
                var b = all[j];
                // Identical prefixes are REQ-VAL-012's error, not this warning.
                if (a.Prefix.PrefixLength == b.Prefix.PrefixLength) continue;
                if (!a.Prefix.Overlaps(b.Prefix)) continue;
                r.Warnf(ReasonCodes.AllowedIPsOverlap, "peers.allowed_ips",
                    $"{a.Prefix} on {a.Peer} overlaps {b.Prefix} on {b.Peer} at a different prefix length");
            }
        }
    }

    // REQ-VAL-031 — valid for site-to-site, usually a mistake otherwise.
    private static void CheckAllowedIPsOutOfSubnet(ValidationResult r, InterfaceSpec spec, IReadOnlyList<Peer> peers)
    {
        var subnets = new List<Cidr>();
        foreach (string s in spec.Addresses)
        {
            if (Cidr.TryParse(s, out var p, out _)) subnets.Add(p.Masked());
        }
        if (subnets.Count == 0) return;

        foreach (var p in peers)
        {
            foreach (string s in p.Spec.AllowedIPs)
            {
                if (!Cidr.TryParse(s, out var q, out _)) continue;
                q = q.Masked();
                bool inside = false;
                foreach (var sub in subnets)
                {
                    // Containment, not overlap: a peer prefix wider than the
                    // interface subnet reaches outside it.
                    if (sub.Contains(q)) { inside = true; break; }
                }
                if (!inside)
                    r.Warnf(ReasonCodes.AllowedIPsOutOfSubnet, "peers.allowed_ips",
                        $"{q} on {p.PublicKey} falls outside the interface subnet");
            }
        }
    }

    // REQ-VAL-033 — the kernel stores only the resolved address, so a DNS change
    // does not propagate.
    private static void CheckPeerWarnings(ValidationResult r, Peer p)
    {
        if (p.Spec.Endpoint.Length == 0) return;
        string field = "peers[" + p.PublicKey + "].endpoint";
        if (!TrySplitHostPort(p.Spec.Endpoint, out string host))
        {
            r.Warnf(ReasonCodes.EndpointNotIP, field, $"\"{p.Spec.Endpoint}\" is not host:port");
            return;
        }
        if (!IPAddress.TryParse(host, out _))
            r.Warnf(ReasonCodes.EndpointNotIP, field,
                $"\"{p.Spec.Endpoint}\" is a hostname; the kernel stores only the address it resolves to now");
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    // IPv4-only per ADR-0005. An IPv4-mapped IPv6 address is treated as IPv4, so
    // the rule matches netip's Is4In6 exemption.
    private static bool IsIPv6(Cidr c) => c.IsIPv6 && !c.Address.IsIPv4MappedToIPv6;

    // Splits an "host:port" or "[host]:port" endpoint, returning the host. It is
    // the parse net.SplitHostPort performs, enough for REQ-VAL-033's warning.
    private static bool TrySplitHostPort(string endpoint, out string host)
    {
        host = "";
        if (endpoint.StartsWith('['))
        {
            int close = endpoint.IndexOf(']');
            if (close < 0 || close + 1 >= endpoint.Length || endpoint[close + 1] != ':') return false;
            host = endpoint[1..close];
            return endpoint.Length > close + 2;
        }
        int colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || colon == endpoint.Length - 1) return false;
        if (endpoint.IndexOf(':') != colon) return false; // more than one colon and unbracketed: ambiguous.
        host = endpoint[..colon];
        return true;
    }

    private static string AxisWire(Axis a) => a switch
    {
        Axis.Allow => "ALLOW",
        Axis.Deny => "DENY",
        Axis.AllowList => "ALLOW_LIST",
        _ => a.ToString(),
    };
}

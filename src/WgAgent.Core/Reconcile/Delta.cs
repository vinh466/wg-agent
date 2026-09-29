using WgAgent.Core.Model;
using WgAgent.Platform;

namespace WgAgent.Core.Reconcile;

// The field-ownership rules of section 3 of SPEC-03. Every comparison here
// answers one question: has an agent-owned field drifted? A kernel-owned one is
// never compared, which is REQ-RCN-012. A malformed stored value surfaces as a
// FormatException, which the engine turns into a STORE_CORRUPT condition.
internal static class Delta
{
    internal readonly record struct DeviceDelta(Key? PrivateKey, int? ListenPort, uint? Fwmark, bool Changed);

    /// <summary>
    /// The device-level part of a Configure call — the agent-owned fields
    /// private_key, listen_port and fwmark. A null field already matches, so a
    /// pass over an interface in the state its spec asks for produces an empty
    /// delta and writes nothing — REQ-RES-003.
    /// </summary>
    internal static DeviceDelta Device(InterfaceSpec spec, DeviceState ds)
    {
        Key? privateKey = null;
        int? listenPort = null;
        uint? fwmark = null;
        bool changed = false;

        if (spec.PrivateKey.Length != 0 && spec.PrivateKey != ds.PrivateKey.ToBase64())
        {
            privateKey = Key.Parse(spec.PrivateKey);
            changed = true;
        }
        if (spec.ListenPort != 0 && spec.ListenPort != ds.ListenPort)
        {
            listenPort = spec.ListenPort;
            changed = true;
        }
        if (spec.Fwmark != ds.Fwmark)
        {
            fwmark = spec.Fwmark;
            changed = true;
        }
        return new DeviceDelta(privateKey, listenPort, fwmark, changed);
    }

    /// <summary>
    /// Diffs the stored peer set against the kernel's, by public key.
    /// </summary>
    /// <remarks>
    /// Step 4 of REQ-RCN-022 names the three cases. REQ-RCN-023 forbids
    /// whole-list replacement here, so removals are explicit and every update
    /// carries UpdateOnly: a delta must not resurrect a peer another writer
    /// removed between the snapshot and the write.
    /// </remarks>
    internal static List<PeerConfig> Peers(IReadOnlyList<Peer> peers, DeviceState ds)
    {
        // Keyed by base64 rather than Key: Key.GetHashCode is deliberately weak,
        // so it must not be a dictionary discriminator.
        var kernel = new Dictionary<string, PeerState>(ds.Peers.Count);
        foreach (var p in ds.Peers) kernel[p.PublicKey.ToBase64()] = p;
        var desired = new HashSet<string>();

        var output = new List<PeerConfig>();
        foreach (var want in peers)
        {
            desired.Add(want.PublicKey);
            var allowed = ParseNetworks(want.Spec.AllowedIPs);
            var keepalive = TimeSpan.FromSeconds(want.Spec.PersistentKeepalive);
            var key = Key.Parse(want.PublicKey);

            if (!kernel.TryGetValue(want.PublicKey, out var have))
            {
                // Creation is the one moment REQ-RCN-013 permits an endpoint.
                var create = new PeerConfig
                {
                    PublicKey = key,
                    AllowedIPs = allowed,
                    ReplaceAllowedIPs = true,
                    PresharedKey = ParsePsk(want.Spec.PresharedKey),
                };
                if (want.Spec.Endpoint.Length != 0) create = create with { Endpoint = want.Spec.Endpoint };
                if (keepalive != TimeSpan.Zero) create = create with { PersistentKeepalive = keepalive };
                output.Add(create);
                continue;
            }

            // Present in both. Compare the agent-owned fields alone; the
            // endpoint is kernel-owned and REQ-RCN-051 forbids writing it.
            var pc = new PeerConfig { PublicKey = key, UpdateOnly = true };
            bool drifted = false;

            if (!SamePrefixes(allowed, have.AllowedIPs))
            {
                pc = pc with { AllowedIPs = allowed, ReplaceAllowedIPs = true };
                drifted = true;
            }
            if (want.Spec.PresharedKey != have.PresharedKey.ToBase64())
            {
                pc = pc with { PresharedKey = ParsePsk(want.Spec.PresharedKey) };
                drifted = true;
            }
            if (keepalive != have.PersistentKeepalive)
            {
                pc = pc with { PersistentKeepalive = keepalive };
                drifted = true;
            }
            if (drifted) output.Add(pc);
        }

        // Present in the kernel only. Sorted so a pass is deterministic, which
        // is what lets a test assert the recorded call sequence.
        var extra = kernel.Keys.Where(k => !desired.Contains(k)).OrderBy(k => k, StringComparer.Ordinal);
        foreach (string k in extra)
            output.Add(new PeerConfig { PublicKey = Key.Parse(k), Remove = true });

        return output;
    }

    /// <summary>
    /// The preshared key, translating an empty spec value into an absent one.
    /// The kernel has no "unset" message for a preshared key, so an empty field
    /// on a peer the kernel holds a key for has to send zeros rather than say
    /// nothing, or the key would survive its removal from desired state.
    /// </summary>
    private static Key ParsePsk(string b64) => b64.Length == 0 ? Key.None : Key.Parse(b64);

    /// <summary>The addresses to add and to remove. Addresses are agent-owned — REQ-RCN-011.</summary>
    internal static (List<Cidr> Add, List<Cidr> Del) Addresses(InterfaceSpec spec, LinkState ls)
        => DiffPrefixes(ParseAddrs(spec.Addresses), ls.Addresses);

    /// <summary>
    /// The routes to add and to remove, where the desired set is the union of
    /// every peer's allowed_ips — step 8 of REQ-RCN-022.
    /// </summary>
    internal static (List<Cidr> Add, List<Cidr> Del) Routes(IReadOnlyList<Peer> peers, IReadOnlyList<Cidr> have)
    {
        var seen = new HashSet<Cidr>();
        var want = new List<Cidr>();
        foreach (var p in peers)
        {
            foreach (var x in ParseNetworks(p.Spec.AllowedIPs))
            {
                if (seen.Add(x)) want.Add(x);
            }
        }
        return DiffPrefixes(want, have);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static (List<Cidr> Add, List<Cidr> Del) DiffPrefixes(IReadOnlyList<Cidr> want, IReadOnlyList<Cidr> have)
    {
        var haveSet = new HashSet<Cidr>(have);
        var wantSet = new HashSet<Cidr>(want);

        var add = want.Where(p => !haveSet.Contains(p)).ToList();
        var del = have.Where(p => !wantSet.Contains(p)).ToList();
        SortPrefixes(add);
        SortPrefixes(del);
        return (add, del);
    }

    // Interface addresses keep their host bits: 10.0.0.1/24 is the address to
    // assign, and masking it to 10.0.0.0/24 would put the network address on
    // the interface instead.
    private static List<Cidr> ParseAddrs(IReadOnlyList<string> ss) => Parse(ss, mask: false);

    // allowed_ips and routes are masked: both the WireGuard device and the
    // routing table normalise a prefix that way, so an unmasked value would
    // read back different from what was written and drift on every pass.
    private static List<Cidr> ParseNetworks(IReadOnlyList<string> ss) => Parse(ss, mask: true);

    private static List<Cidr> Parse(IReadOnlyList<string> ss, bool mask)
    {
        var output = new List<Cidr>(ss.Count);
        foreach (string s in ss)
        {
            var p = Cidr.Parse(s);
            output.Add(mask ? p.Masked() : p);
        }
        return output;
    }

    private static bool SamePrefixes(IReadOnlyList<Cidr> a, IReadOnlyList<Cidr> b)
    {
        if (a.Count != b.Count) return false;
        var x = a.ToList();
        var y = b.ToList();
        SortPrefixes(x);
        SortPrefixes(y);
        for (int i = 0; i < x.Count; i++)
        {
            if (x[i] != y[i]) return false;
        }
        return true;
    }

    private static void SortPrefixes(List<Cidr> ps)
        => ps.Sort((i, j) => string.CompareOrdinal(i.ToString(), j.ToString()));
}

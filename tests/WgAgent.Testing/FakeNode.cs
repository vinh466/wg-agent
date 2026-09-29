using WgAgent.Platform;

namespace WgAgent.Testing;

/// <summary>
/// An in-memory model of one host's WireGuard state, implementing the platform
/// ports.
/// </summary>
/// <remarks>
/// This is what keeps the rule that only WgAgent.Platform.Linux needs
/// privilege: a test above that layer builds the node it wants here and runs as
/// an ordinary user in milliseconds. A test that reaches for root above the
/// adapters is a sign the seam has leaked. <see cref="Calls"/> records every
/// mutation in order, so a test can assert what was <em>not</em> done —
/// REQ-RCN-012 and REQ-RCN-051 are both statements about writes that must not
/// happen.
/// </remarks>
public sealed partial class FakeNode : IDevice, IHostFs, IDesiredState
{
    public Dictionary<string, LinkState> Links { get; } = [];
    public Dictionary<string, DeviceState> Devices { get; } = [];
    public Dictionary<string, UnitState> Units { get; } = [];
    public Dictionary<string, WgQuickConfig> Configs { get; } = [];
    public HashSet<string> Managed { get; } = [];
    public HashSet<string> Deleted { get; } = [];
    public Dictionary<string, string> Forwarding { get; } = [];
    public Dictionary<string, List<Cidr>> RouteTable { get; } = [];

    /// <summary>
    /// Makes every forwarding write fail, which is what an ordinary container
    /// does and what REQ-CFG-011 exists to carve out.
    /// </summary>
    public bool SysctlReadOnly { get; set; }

    public List<string> Calls { get; } = [];

    /// <summary>
    /// A distinct non-zero key. Zeros would read back as absent, because
    /// <see cref="Key.FromBytes"/> follows the kernel in treating an all-zero
    /// key as no key at all.
    /// </summary>
    internal static Key TestKey(byte seed)
    {
        Span<byte> b = stackalloc byte[Key.Size];
        b.Fill(seed);
        return Key.FromBytes(b);
    }

    /// <summary>A <see cref="ILink"/> view over this node.</summary>
    public FakeLink Link() => new(this);

    // ── builders ────────────────────────────────────────────────────────────

    /// <summary>Registers a WireGuard interface with one address and no peers.</summary>
    public FakeNode AddInterface(string name, string cidr, int port)
    {
        var p = Cidr.Parse(cidr);
        Links[name] = new LinkState { Name = name, Type = "wireguard", Mtu = 1420, AdminUp = true, Addresses = [p] };
        var priv = TestKey(1);
        Devices[name] = new DeviceState { Name = name, PrivateKey = priv, PublicKey = priv.GetPublicKey(), ListenPort = port };
        return this;
    }

    /// <summary>Appends a peer to an interface.</summary>
    public FakeNode AddPeer(string iface, string publicKey, string allowed = "", string endpoint = "", bool psk = false)
    {
        var d = Devices[iface];
        var peer = new PeerState
        {
            PublicKey = Key.Parse(publicKey),
            Endpoint = endpoint,
            PersistentKeepalive = TimeSpan.FromSeconds(25),
        };
        if (psk) peer = peer with { PresharedKey = TestKey(2) };
        if (allowed.Length > 0) peer = peer with { AllowedIPs = [Cidr.Parse(allowed)] };
        Devices[iface] = d with { Peers = [.. d.Peers, peer] };
        return this;
    }

    public FakeNode WithAddress(string iface, params string[] cidrs)
    {
        Links[iface] = Links[iface] with { Addresses = [.. cidrs.Select(Cidr.Parse)] };
        return this;
    }

    /// <summary>Overrides the netlink type, for the not-a-WireGuard-link case.</summary>
    public FakeNode WithLinkType(string iface, string type)
    {
        Links[iface] = Links[iface] with { Type = type };
        return this;
    }

    public FakeNode WithUnit(string iface, UnitState s) { Units[iface] = s; return this; }
    public FakeNode WithConfig(string iface, WgQuickConfig c) { Configs[iface] = c; return this; }
    public FakeNode WithManaged(string iface) { Managed.Add(iface); return this; }
    public FakeNode WithDeletionRecord(string iface) { Deleted.Add(iface); return this; }
    public FakeNode WithForwarding(string iface, string v) { Forwarding[iface] = v; return this; }
    public FakeNode WithReadOnlySysctl() { SysctlReadOnly = true; return this; }

    // ── IDevice (read) ────────────────────────────────────────────────────────

    public IReadOnlyList<string> Names()
    {
        var names = new List<string>(Devices.Keys);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    public DeviceState Snapshot(string name)
        => Devices.TryGetValue(name, out var d) ? d : throw new InvalidOperationException($"no such device \"{name}\"");

    // ── IHostFs ─────────────────────────────────────────────────────────────

    public UnitState WgQuickUnit(string iface) => Units.TryGetValue(iface, out var s) ? s : UnitState.Absent;

    public WgQuickConfig WgQuickConfig(string iface) => Configs.TryGetValue(iface, out var c) ? c : new WgQuickConfig();

    public string ForwardingSysctl(string iface) => Forwarding.TryGetValue(iface, out var v) ? v : "";

    /// <summary>
    /// Records the write, and refuses when <see cref="SysctlReadOnly"/> is set —
    /// the ordinary container case, and what <c>ProtectKernelTunables=yes</c>
    /// produces without the carve-out of REQ-CFG-011.
    /// </summary>
    public void SetForwardingSysctl(string iface, string value)
    {
        // The node goes with the link — REQ-FWD-020's per-interface node.
        if (!Links.ContainsKey(iface)) return;
        if (Forwarding.TryGetValue(iface, out var cur) && cur == value) return;
        if (SysctlReadOnly)
            throw new IOException($"write forwarding sysctl of \"{iface}\": read-only file system");
        Forwarding[iface] = value;
        Calls.Add($"SetForwarding({iface},{value})");
    }

    // ── IDesiredState ─────────────────────────────────────────────────────────

    public bool Describes(string name) => Managed.Contains(name);
    public bool HasDeletionRecord(string name) => Deleted.Contains(name);

    // ── call log ──────────────────────────────────────────────────────────────

    /// <summary>Whether any recorded call contains the substring.</summary>
    public bool Did(string substr) => Calls.Any(c => c.Contains(substr, StringComparison.Ordinal));

    /// <summary>Clears the record, so a test can assert what a second pass did.</summary>
    public void ResetCalls() => Calls.Clear();

    internal static string Short(Key k)
    {
        string b = k.ToBase64();
        return b.Length <= 8 ? b : b[..8];
    }
}

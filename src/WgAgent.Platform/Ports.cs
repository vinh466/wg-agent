namespace WgAgent.Platform;

/// <summary>
/// A delta applied to a WireGuard device. A null property leaves the field as
/// the kernel holds it, which is what lets REQ-RCN-011 enforce the agent-owned
/// fields without touching anything else.
/// </summary>
public sealed record DeviceConfig
{
    public Key? PrivateKey { get; init; }
    public int? ListenPort { get; init; }
    public uint? Fwmark { get; init; }
    public IReadOnlyList<PeerConfig> Peers { get; init; } = [];

    /// <summary>
    /// Removes every peer the list omits, and clears the endpoint of every peer
    /// it keeps. REQ-RCN-023 confines it to BatchUpdatePeers with
    /// <c>replace_all</c>; reconcile uses deltas so a learned endpoint survives.
    /// </summary>
    public bool ReplacePeers { get; init; }
}

/// <summary>A delta applied to one peer, keyed by public key.</summary>
public sealed record PeerConfig
{
    public required Key PublicKey { get; init; }
    public bool Remove { get; init; }

    /// <summary>
    /// Refuses to create the peer if it is absent, which keeps a delta from
    /// resurrecting one another writer removed.
    /// </summary>
    public bool UpdateOnly { get; init; }

    public Key? PresharedKey { get; init; }
    public IReadOnlyList<Cidr>? AllowedIPs { get; init; }

    /// <summary>Makes <see cref="AllowedIPs"/> the whole set rather than an addition.</summary>
    public bool ReplaceAllowedIPs { get; init; }

    /// <summary>
    /// Applied at peer creation only. REQ-RCN-013 permits it when the spec
    /// changes, which is the write path rather than reconcile, and REQ-RCN-051
    /// forbids reconcile from overwriting one the kernel learned.
    /// </summary>
    public string? Endpoint { get; init; }

    public TimeSpan? PersistentKeepalive { get; init; }
}

/// <summary>
/// Reads and configures a WireGuard device. It cannot create or destroy the
/// interface itself — that is <see cref="ILink"/>, and the split is the one
/// docs/00-overview/architecture.md fixes.
/// </summary>
public interface IDevice
{
    /// <summary>The WireGuard devices the kernel holds.</summary>
    IReadOnlyList<string> Names();

    /// <summary>One device and all of its peers.</summary>
    DeviceState Snapshot(string name);

    /// <summary>Applies a delta.</summary>
    void Configure(string name, DeviceConfig config);
}

/// <summary>Owns interface lifecycle, addressing and routes.</summary>
public interface ILink
{
    /// <summary>Every network interface, whatever its type.</summary>
    IReadOnlyList<string> Names();

    /// <summary>One interface, including its addresses.</summary>
    LinkState State(string name);

    /// <summary>Creates a WireGuard link. The device port cannot do this.</summary>
    void Add(string name);

    void Delete(string name);

    /// <summary>Drives the administrative flag REQ-RES-019 reads.</summary>
    void SetUp(string name);

    void SetDown(string name);
    void SetMtu(string name, int mtu);

    void AddAddress(string name, Cidr address);
    void DeleteAddress(string name, Cidr address);

    /// <summary>
    /// The routes whose device is this interface, excluding the ones the kernel
    /// derives from its addresses. Step 8 of REQ-RCN-022 removes what the union
    /// of <c>allowed_ips</c> omits, and the interface's own subnet route is not
    /// the agent's to remove.
    /// </summary>
    IReadOnlyList<Cidr> Routes(string name);

    void AddRoute(string name, Cidr destination);
    void DeleteRoute(string name, Cidr destination);
}

/// <summary>
/// The netlink subscription REQ-RCN-021 requires, so an externally deleted link
/// is noticed rather than waited for.
/// </summary>
/// <remarks>
/// A port of its own rather than a method on <see cref="ILink"/>: an agent that
/// only reconciles on the timer still works, so a caller with no use for it can
/// leave the dependency unset.
/// </remarks>
public interface ILinkEvents
{
    /// <summary>Delivers events until the token is cancelled.</summary>
    IAsyncEnumerable<LinkEvent> SubscribeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The filesystem questions the readiness report asks. A port so the report can
/// be tested without a systemd tree on disk.
/// </summary>
public interface IHostFs
{
    /// <summary>Whether <c>wg-quick@&lt;iface&gt;.service</c> is enabled.</summary>
    UnitState WgQuickUnit(string iface);

    /// <summary>The unsupported directives of the interface's configuration file.</summary>
    WgQuickConfig WgQuickConfig(string iface);

    /// <summary>
    /// The interface's current forwarding value, which REQ-FWD-024 records at
    /// adoption and restores on release. An empty string means the value could
    /// not be read.
    /// </summary>
    string ForwardingSysctl(string iface);

    /// <summary>
    /// Writes it. Step 9 of REQ-RCN-022 calls this under REQ-FWD-020, and
    /// release calls it under REQ-FWD-024. The per-interface node exists only
    /// while the link does, so writing to an interface with no node is not an
    /// error — there is nothing to configure.
    /// </summary>
    void SetForwardingSysctl(string iface, string value);
}

/// <summary>
/// The part of the store the readiness report needs. <c>doctor</c> reads it
/// directly under REQ-CLI-004, and an absent store is an empty desired state
/// rather than an error, which is what makes the command usable on a node where
/// the agent has never run.
/// </summary>
public interface IDesiredState
{
    /// <summary>Whether desired state describes the interface — REQ-RES-017's MANAGED.</summary>
    bool Describes(string name);

    /// <summary>Whether a deletion record names it — REQ-RCN-034's ORPHANED.</summary>
    bool HasDeletionRecord(string name);
}

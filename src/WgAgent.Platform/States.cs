namespace WgAgent.Platform;

/// <summary>
/// One WireGuard device as the kernel holds it. A single read returns every
/// field adoption stores under REQ-RCN-061 and REQ-RCN-062.
/// </summary>
public sealed record DeviceState
{
    public required string Name { get; init; }
    public Key PrivateKey { get; init; }
    public Key PublicKey { get; init; }
    public int ListenPort { get; init; }
    public uint Fwmark { get; init; }
    public IReadOnlyList<PeerState> Peers { get; init; } = [];
}

/// <summary>One peer as the kernel holds it.</summary>
public sealed record PeerState
{
    public required Key PublicKey { get; init; }
    public Key PresharedKey { get; init; }
    public IReadOnlyList<Cidr> AllowedIPs { get; init; } = [];

    /// <summary>
    /// Kernel-owned: the kernel learns it from a handshake and does not
    /// distinguish that from one an operator configured. REQ-RCN-063 keeps it
    /// out of desired state; REQ-DIA-048 warns when one is present.
    /// </summary>
    public string Endpoint { get; init; } = "";

    public TimeSpan PersistentKeepalive { get; init; }

    /// <summary>
    /// Runtime fields, read from the kernel under REQ-RES-024 and never stored:
    /// REQ-RCN-050 keeps a traffic counter out of desired state.
    /// <see cref="LastHandshake"/> is null when no handshake has occurred,
    /// which SPEC-01 section 4.3 reports as a null handshake age rather than a
    /// zero one.
    /// </summary>
    public DateTimeOffset? LastHandshake { get; init; }

    public long ReceiveBytes { get; init; }
    public long TransmitBytes { get; init; }
    public int ProtocolVersion { get; init; }
}

/// <summary>One network interface as netlink holds it.</summary>
public sealed record LinkState
{
    public required string Name { get; init; }

    /// <summary>
    /// The netlink link type. <c>wireguard</c> is what REQ-DIA-042 needs in
    /// order to reject a link the resource model does not describe.
    /// </summary>
    public string Type { get; init; } = "";

    public int Mtu { get; init; }

    /// <summary>
    /// The administrative flag. REQ-RES-019 requires <c>oper_state</c> to come
    /// from here, because a WireGuard link reports its operational state as
    /// unknown even while it is up.
    /// </summary>
    public bool AdminUp { get; init; }

    public IReadOnlyList<Cidr> Addresses { get; init; } = [];
}

/// <summary>One interface the kernel changed.</summary>
/// <param name="Name">The interface.</param>
/// <param name="Deleted">
/// Distinguishes a link that is gone from one brought down. Both are reconcile
/// triggers under REQ-RCN-020; only the first ends a link.
/// </param>
public readonly record struct LinkEvent(string Name, bool Deleted);

/// <summary>
/// What can be learned about a systemd unit without executing anything.
/// REQ-SEC-041 forbids a child process in a production path, so enablement is
/// read as a symlink under a target's wants directory — REQ-DIA-049.
/// </summary>
public enum UnitState
{
    /// <summary>
    /// The search roots could not be read. REQ-DIA-050 requires this to surface
    /// as UNKNOWN rather than as a pass.
    /// </summary>
    Undetermined = 0,
    Absent,
    Disabled,
    Enabled,
}

/// <summary>
/// The directives a wg-quick configuration file carries. Values are never read
/// from it: REQ-DIA-045 keeps the file out of the spec, and every field comes
/// from the kernel under REQ-RCN-061.
/// </summary>
public sealed record WgQuickConfig
{
    public string Path { get; init; } = "";

    /// <summary>
    /// False when no file exists for the interface, which is not a finding of
    /// any kind.
    /// </summary>
    public bool Present { get; init; }

    /// <summary>
    /// True when the file exists but could not be read. A WARN under
    /// REQ-DIA-046, never a blocking finding.
    /// </summary>
    public bool Unparseable { get; init; }

    /// <summary>
    /// The directives found that the resource model has no equivalent for, in
    /// the order encountered.
    /// </summary>
    public IReadOnlyList<string> Directives { get; init; } = [];
}

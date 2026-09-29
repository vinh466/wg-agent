using System.Text.Json.Serialization;

namespace WgAgent.Core.Model;

// The resource model of SPEC-01 and the two policy shapes of SPEC-02.
//
// ADR-0003 makes the .proto the source of truth for these types, so this is
// what the generated ones map onto at the API layer. It exists below the
// service so the store has a spec shape to persist, and a layer below the
// service cannot depend on one above it. The JSON names match the store format
// the read and write sides share.

/// <summary>
/// A forward-policy axis value. SPEC-02 section 2 fixes the set;
/// <see cref="AllowList"/> applies to <c>inter_interface</c> alone.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Axis>))]
public enum Axis
{
    [JsonStringEnumMemberName("ALLOW")]
    Allow,

    [JsonStringEnumMemberName("DENY")]
    Deny,

    [JsonStringEnumMemberName("ALLOW_LIST")]
    AllowList,
}

/// <summary>
/// The forward policy of SPEC-02 section 2. The defaults of REQ-FWD-001 are not
/// applied here: REQ-RCN-066 requires adoption to take these from the request,
/// because a default would be a guess applied to a live node. Construct the
/// create-time default through <see cref="Default"/>.
/// </summary>
public sealed record ForwardPolicySpec
{
    [JsonPropertyName("intra_interface")]
    public Axis IntraInterface { get; init; }

    [JsonPropertyName("inter_interface")]
    public Axis InterInterface { get; init; }

    [JsonPropertyName("allowed_peer_interfaces")]
    public IReadOnlyList<string> AllowedPeerInterfaces { get; init; } = [];

    [JsonPropertyName("external")]
    public Axis External { get; init; }

    /// <summary>
    /// REQ-FWD-001: the value a create uses, not one adoption may fall back to.
    /// </summary>
    public static ForwardPolicySpec Default() => new()
    {
        IntraInterface = Axis.Allow,
        InterInterface = Axis.Deny,
        External = Axis.Deny,
    };
}

/// <summary>The NAT policy of SPEC-02 section 6.</summary>
public sealed record NatSpec
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("masquerade_out_interface")]
    public string MasqueradeOutInterface { get; init; } = "";

    [JsonPropertyName("enable_uplink_forwarding")]
    public bool EnableUplinkForwarding { get; init; }
}

/// <summary>
/// An interface spec, SPEC-01 section 3.2.
/// </summary>
/// <remarks>
/// It carries no name: REQ-RES-034 keeps identity outside both spec and status,
/// so a FOREIGN interface returned with its spec absent (REQ-RCN-031) is still
/// addressable. <see cref="PrivateKey"/> is write-only — REQ-RES-013 keeps it
/// out of every response, and REQ-DIA-047 reports only that it is present.
/// </remarks>
public sealed record InterfaceSpec
{
    [JsonPropertyName("private_key")]
    public string PrivateKey { get; init; } = "";

    [JsonPropertyName("listen_port")]
    public int ListenPort { get; init; }

    [JsonPropertyName("addresses")]
    public IReadOnlyList<string> Addresses { get; init; } = [];

    [JsonPropertyName("mtu")]
    public int Mtu { get; init; }

    [JsonPropertyName("fwmark")]
    public uint Fwmark { get; init; }

    [JsonPropertyName("manage_routes")]
    public bool ManageRoutes { get; init; }

    [JsonPropertyName("forward_policy")]
    public ForwardPolicySpec ForwardPolicy { get; init; } = new();

    [JsonPropertyName("nat")]
    public NatSpec Nat { get; init; } = new();

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

/// <summary>
/// A peer spec, SPEC-01 section 4.2.
/// </summary>
/// <remarks>
/// <see cref="Endpoint"/> is kernel-owned under REQ-RCN-013 and REQ-RCN-051.
/// Adoption never stores one (REQ-RCN-063), so the field is present for a create
/// or update that sets it deliberately.
/// </remarks>
public sealed record PeerSpec
{
    [JsonPropertyName("preshared_key")]
    public string PresharedKey { get; init; } = "";

    [JsonPropertyName("allowed_ips")]
    public IReadOnlyList<string> AllowedIPs { get; init; } = [];

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; init; } = "";

    [JsonPropertyName("persistent_keepalive")]
    public int PersistentKeepalive { get; init; }

    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

/// <summary>
/// A peer's identity paired with its spec. REQ-RES-020 makes the pair
/// (interface_name, public_key) the identity, and REQ-RES-034 keeps it out of
/// the spec.
/// </summary>
public sealed record Peer
{
    [JsonPropertyName("interface_name")]
    public string InterfaceName { get; init; } = "";

    [JsonPropertyName("public_key")]
    public string PublicKey { get; init; } = "";

    [JsonPropertyName("spec")]
    public PeerSpec Spec { get; init; } = new();
}

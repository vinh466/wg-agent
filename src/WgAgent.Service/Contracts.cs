using WgAgent.Core.Model;
using WgAgent.Platform;

namespace WgAgent.Service;

/// <summary>The settings the operations read — SPEC-09 section 2.</summary>
public sealed record AgentOptions
{
    public TimeSpan ApplyTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PeerOnlineThreshold { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>node.endpoint: the host clients reach this node at (REQ-KEY-037).</summary>
    public string? NodeEndpoint { get; init; }
}

/// <summary>The host, through its ports, and the deadline its adapters share.</summary>
public sealed record HostPorts(IWireGuardTool Wg, IUnitManager Units, IConfigDirectory Files, IHostNetwork Network, ApplyDeadline Deadline);

/// <summary>A write's result, and whether it interrupted the interface's sessions (REQ-APL-007).</summary>
public sealed record WriteResult<T>(T Resource, bool Restarted);

/// <summary>CreatePeer: a public key (REQ-KEY-010), or a key pair the agent generates (REQ-KEY-011).</summary>
public sealed record CreatePeerRequest
{
    public string? PublicKey { get; init; }
    public bool GenerateKeypair { get; init; }
    public bool GeneratePresharedKey { get; init; }
    public PeerSpec Spec { get; init; } = new();
    public IReadOnlyList<string>? ClientAllowedIps { get; init; }
    public uint? ClientPersistentKeepalive { get; init; }
    public IReadOnlyList<string>? Dns { get; init; }
    public string? NodeEndpoint { get; init; }
}

/// <summary>
/// CreatePeer's answer. The generated keys and the client configuration exist only here — the
/// private key is never stored (REQ-KEY-012), and no later response returns either key.
/// </summary>
public sealed record CreatePeerResult(
    PeerResource Peer,
    bool Restarted,
    SecretKey? GeneratedPrivateKey,
    SecretKey? GeneratedPresharedKey,
    string? ClientConfiguration);

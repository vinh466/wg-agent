namespace WgAgent.Platform;

/// <summary>A failure reported by the host — a program's exit status and its message.</summary>
public sealed class PlatformException(string message) : Exception(message);

/// <summary>
/// The <c>wg</c> program, as far as the agent uses it (ADR-0013). Keys cross only on standard input
/// and output (REQ-SEC-089).
/// </summary>
public interface IWireGuardTool
{
    SecretKey GenerateKey();
    SecretKey GeneratePresharedKey();
    PublicKey PublicKeyOf(SecretKey privateKey);

    /// <summary><c>wg syncconf</c>: applies a configuration without the keys only wg-quick reads.</summary>
    void SyncConfig(string interfaceName, string wireGuardConfig);

    /// <summary><c>wg show &lt;name&gt; dump</c>; null when the device does not exist.</summary>
    DeviceDump? Show(string interfaceName);

    /// <summary>The port each WireGuard device on the host holds.</summary>
    IReadOnlyDictionary<string, uint> ListenPorts();
}

/// <summary>The part of <c>wg show dump</c> the agent reports.</summary>
public sealed record DeviceDump(PublicKey PublicKey, uint ListenPort, IReadOnlyList<PeerDump> Peers);

/// <summary>One peer line of the dump. A handshake that never happened is null, never the epoch.</summary>
public sealed record PeerDump(
    PublicKey PublicKey,
    string? Endpoint,
    IReadOnlyList<string> AllowedIps,
    DateTimeOffset? LatestHandshake,
    long RxBytes,
    long TxBytes);

/// <summary>The systemd unit <c>wg-quick@&lt;name&gt;</c> of an interface (REQ-APL-001).</summary>
public interface IUnitManager
{
    void Enable(string interfaceName);
    void Disable(string interfaceName);
    void Start(string interfaceName);
    void Stop(string interfaceName);
    bool IsActive(string interfaceName);
}

/// <summary>
/// The files under /etc/wireguard/. The agent reads, writes and deletes only the files it created
/// (REQ-APL-002); listing the names is how it avoids the others (REQ-VAL-015).
/// </summary>
public interface IConfigDirectory
{
    IReadOnlySet<string> Names();
    string? Read(string interfaceName);

    /// <summary>Owned by root, mode 0600, replaced atomically (REQ-APL-009).</summary>
    void Write(string interfaceName, string content);

    void Delete(string interfaceName);
}

/// <summary>The network links of the host and their IPv4 addresses.</summary>
public interface IHostNetwork
{
    IReadOnlySet<string> LinkNames();
    IReadOnlyList<Cidr> AddressesOf(string linkName);
}

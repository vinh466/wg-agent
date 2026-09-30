using WgAgent.Core.Model;
using WgAgent.Core.Store;
using WgAgent.Platform;

namespace WgAgent.Core.Status;

/// <summary>
/// Reads status from the host on every call, accumulating nothing (REQ-RES-024).
/// </summary>
public sealed class StatusReader(IWireGuardTool wg, IUnitManager units, TimeProvider clock, TimeSpan onlineThreshold)
{
    /// <summary>What the host holds for one interface, read once and shared by its peers.</summary>
    public sealed record Observation(bool UnitActive, DeviceDump? Device);

    public Observation Observe(string name) => new(units.IsActive(name), wg.Show(name));

    /// <summary>REQ-RES-035 and REQ-RES-036.</summary>
    public static InterfaceStatus Interface(Observation seen, StoredInterface stored, string publicKey, IReadOnlyList<Warning> warnings) => new()
    {
        PublicKey = publicKey,
        ListenPort = seen.Device?.ListenPort,
        CreatedAt = stored.CreatedAt,
        OperState = seen.UnitActive ? (seen.Device is null ? OperState.Absent : OperState.Up) : OperState.Down,
        PeerCount = seen.Device?.Peers.Count,
        Warnings = warnings,
    };

    public PeerStatus Peer(Observation seen, string publicKey, IReadOnlyList<Warning> warnings)
    {
        var dump = seen.Device?.Peers.FirstOrDefault(p => p.PublicKey.ToString() == publicKey);
        var now = clock.GetUtcNow();
        var handshake = dump?.LatestHandshake;
        return new PeerStatus
        {
            LastHandshakeAt = handshake,
            HandshakeAgeSeconds = handshake is { } at ? (long)(now - at).TotalSeconds : null,
            Online = PeerStatus.IsOnline(handshake, now, onlineThreshold),
            RxBytes = dump?.RxBytes,
            TxBytes = dump?.TxBytes,
            ResolvedEndpoint = dump?.Endpoint,
            Warnings = warnings,
        };
    }
}

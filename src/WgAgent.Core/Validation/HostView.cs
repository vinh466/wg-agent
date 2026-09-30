using WgAgent.Platform;

namespace WgAgent.Core.Validation;

/// <summary>
/// What the host holds, gathered through the ports before validation runs, so every rule stays a
/// pure function of its inputs.
/// </summary>
public sealed record HostView
{
    /// <summary>Every network link on the host, by name (REQ-VAL-015).</summary>
    public IReadOnlySet<string> Links { get; init; } = new HashSet<string>();

    /// <summary>The names of the files under /etc/wireguard/, without ".conf" (REQ-VAL-015).</summary>
    public IReadOnlySet<string> ConfigFiles { get; init; } = new HashSet<string>();

    /// <summary>The port each running WireGuard interface holds (REQ-VAL-013).</summary>
    public IReadOnlyDictionary<string, uint> WireGuardListenPorts { get; init; } = new Dictionary<string, uint>();

    /// <summary>The addresses each running WireGuard interface carries (REQ-VAL-014).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Cidr>> WireGuardAddresses { get; init; } = new Dictionary<string, IReadOnlyList<Cidr>>();
}

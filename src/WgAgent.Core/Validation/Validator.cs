using WgAgent.Core.Model;
using WgAgent.Platform;

namespace WgAgent.Core.Validation;

/// <summary>
/// What the rules need to know about the node. REQ-VAL-013 and REQ-VAL-014 reach
/// foreign interfaces as well as managed ones, so this covers every WireGuard
/// interface the kernel holds.
/// </summary>
public sealed class Host
{
    /// <summary>Interface name to its listen port.</summary>
    public Dictionary<string, int> Ports { get; init; } = [];

    /// <summary>Interface name to the addresses its link carries.</summary>
    public Dictionary<string, IReadOnlyList<Cidr>> Addresses { get; init; } = [];

    /// <summary>
    /// Builds the host facts from the platform ports.
    /// </summary>
    /// <remarks>
    /// Only WireGuard devices are listed. REQ-VAL-013 is about a port held by a
    /// WireGuard interface and REQ-VAL-014 about a subnet on one, so a bridge or
    /// an ethernet link carrying the same subnet is outside both.
    /// </remarks>
    public static Host Read(IDevice device, ILink link)
    {
        var host = new Host();
        foreach (string name in device.Names())
        {
            host.Ports[name] = device.Snapshot(name).ListenPort;
            try
            {
                host.Addresses[name] = link.State(name).Addresses;
            }
            catch
            {
                // A device whose link vanished between the two reads is not a
                // collision with anything.
            }
        }
        return host;
    }
}

/// <summary>The part of the store the rules read.</summary>
public interface IDesired
{
    /// <summary>
    /// The interfaces desired state describes — the set REQ-VAL-022 tests
    /// membership of.
    /// </summary>
    IReadOnlyList<string> Names();

    /// <summary>
    /// One stored spec, which REQ-VAL-023 needs to see the other side of an
    /// ALLOW_LIST relationship. False when desired state does not describe it.
    /// </summary>
    bool TryGetInterface(string name, out InterfaceSpec spec);

    /// <summary>The REQ-VAL-015 exemption.</summary>
    bool DeletionRecord(string name);
}

/// <summary>Holds the facts every rule shares.</summary>
public sealed partial class Validator(Host host, IDesired? desired)
{
    private readonly Host _host = host;
    private readonly IDesired? _desired = desired;

    /// <summary>
    /// Validates an interface spec together with its peers.
    /// </summary>
    /// <remarks>
    /// The order is the order of section 3 of SPEC-07, then section 4: an error
    /// found first is the reason code the API returns, and a deterministic order
    /// is what makes that reproducible.
    /// </remarks>
    public ValidationResult Interface(string name, InterfaceSpec spec, IReadOnlyList<Peer> peers, Op op)
    {
        var r = new ValidationResult();

        // Errors.
        CheckName(r, name);
        CheckAddresses(r, name, spec);
        CheckListenPort(r, name, spec);
        CheckCreateCollision(r, name, op);
        CheckForwardPolicy(r, spec);
        CheckPeerInterfaces(r, spec);
        foreach (var p in peers) CheckPeerErrors(r, p);
        CheckDuplicateAllowedIPs(r, peers);

        // Warnings.
        CheckMtu(r, spec);
        CheckOneSidedAllowList(r, name, spec);
        CheckIgnoredPeerInterfaces(r, spec);
        CheckExternalWithoutNat(r, spec);
        CheckOverlappingAllowedIPs(r, peers);
        CheckAllowedIPsOutOfSubnet(r, spec, peers);
        foreach (var p in peers) CheckPeerWarnings(r, p);

        return r;
    }

    /// <summary>
    /// Validates one peer against the interface that holds it and the peers
    /// already stored there. The interface rules are not repeated: a stored spec
    /// has already passed them.
    /// </summary>
    public ValidationResult Peer(InterfaceSpec spec, IReadOnlyList<Peer> existing, Peer p)
    {
        var r = new ValidationResult();

        CheckPeerErrors(r, p);

        // REQ-VAL-012 and REQ-VAL-030 are about the set, so the new peer is
        // evaluated alongside the ones already there. A peer replacing itself is
        // not a duplicate of itself.
        var set = new List<Peer>(existing.Count + 1);
        foreach (var e in existing)
        {
            if (e.PublicKey != p.PublicKey) set.Add(e);
        }
        set.Add(p);

        CheckDuplicateAllowedIPs(r, set);
        CheckOverlappingAllowedIPs(r, set);
        CheckAllowedIPsOutOfSubnet(r, spec, [p]);
        CheckPeerWarnings(r, p);
        return r;
    }

    /// <summary>The desired-state names, sorted, tolerating an absent store.</summary>
    private List<string> SortedNames()
    {
        if (_desired is null) return [];
        var names = new List<string>(_desired.Names());
        names.Sort(StringComparer.Ordinal);
        return names;
    }
}

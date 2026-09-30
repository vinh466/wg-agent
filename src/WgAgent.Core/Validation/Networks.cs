using WgAgent.Platform;

namespace WgAgent.Core.Validation;

/// <summary>The network arithmetic the rules, the client configuration and the apply decision share.</summary>
public static class Networks
{
    /// <summary>An interface's subnets: the networks its addresses belong to (SPEC-01 section 3.2).</summary>
    public static IReadOnlyList<Cidr> SubnetsOf(IEnumerable<string> addresses) =>
        [.. addresses.Select(a => Cidr.TryParse(a, out var c) ? c.Network : (Cidr?)null).OfType<Cidr>()];

    /// <summary>True when one of the subnets contains the entry.</summary>
    public static bool IsWithin(Cidr entry, IEnumerable<Cidr> subnets) => subnets.Any(s => s.Contains(entry));

    /// <summary>
    /// REQ-KEY-047: the lowest host address of <paramref name="subnet"/> held neither by an interface
    /// address nor by any peer's allowed IPs; null when none is free (REQ-VAL-046).
    /// </summary>
    public static Cidr? LowestFreeHost(Cidr subnet, IEnumerable<Cidr> interfaceAddresses, IEnumerable<Cidr> peerAllowedIps)
    {
        if (subnet.IsIPv6) return null;
        var network = subnet.Network;
        var first = Cidr.ToUInt32(network.Address);
        var size = subnet.PrefixLength == 0 ? 1UL << 32 : 1UL << (32 - subnet.PrefixLength);
        // Network and broadcast addresses are not hosts, except in the /31 and /32 cases.
        ulong lo = size >= 4 ? 1UL : 0UL, hi = size >= 4 ? size - 2 : size - 1;
        var held = interfaceAddresses.Select(a => Cidr.ToUInt32(a.Address)).ToHashSet();
        var peers = peerAllowedIps.ToList();
        for (var offset = lo; offset <= hi; offset++)
        {
            var candidate = (uint)(first + offset);
            if (held.Contains(candidate)) continue;
            var host = new Cidr(Cidr.FromUInt32(candidate), 32);
            if (peers.Any(p => p.Contains(host))) continue;
            return host;
        }
        return null;
    }
}

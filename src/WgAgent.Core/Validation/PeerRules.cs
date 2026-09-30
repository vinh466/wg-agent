using System.Net;
using System.Text.RegularExpressions;
using WgAgent.Core.Model;
using WgAgent.Platform;

namespace WgAgent.Core.Validation;

/// <summary>What a peer is checked against.</summary>
public sealed record PeerCheck
{
    /// <summary>The interface's spec, defaults applied.</summary>
    public required InterfaceSpec Interface { get; init; }

    /// <summary>The interface's own public key, for REQ-VAL-040.</summary>
    public PublicKey? InterfacePublicKey { get; init; }

    /// <summary>The peer's public key as given; null when the agent generates the key pair.</summary>
    public string? PublicKey { get; init; }

    public required PeerSpec Spec { get; init; }

    /// <summary>True when the request asks the agent to generate the key pair (REQ-KEY-011).</summary>
    public bool GenerateKeypair { get; init; }

    /// <summary>The interface's other peers.</summary>
    public IReadOnlyList<PeerSpec> OtherPeers { get; init; } = [];

    public IReadOnlyList<string>? ClientAllowedIps { get; init; }
    public uint? ClientPersistentKeepalive { get; init; }
    public IReadOnlyList<string>? Dns { get; init; }
}

/// <summary>The peer-level rules of SPEC-07, in the order the module lists them.</summary>
public static partial class PeerRules
{
    public static ValidationResult Check(PeerCheck check)
    {
        var error = FirstError(check);
        return new ValidationResult(error, error is null ? Warnings(check.Interface, check.Spec, check.OtherPeers) : []);
    }

    /// <summary>The peer's warning rules (REQ-VAL-030, 031, 033), also read for status.warnings.</summary>
    public static IReadOnlyList<Finding> Warnings(InterfaceSpec iface, PeerSpec spec, IReadOnlyList<PeerSpec> otherPeers)
    {
        var warnings = new List<Finding>();
        var entries = Parsed(spec.AllowedIps);
        var subnets = Networks.SubnetsOf(iface.Addresses ?? []);

        // REQ-VAL-030: an entry overlapping, at a differing length, one of this or another peer's.
        var all = entries.Concat(otherPeers.SelectMany(p => Parsed(p.AllowedIps))).ToList();
        foreach (var entry in entries)
        {
            var clash = all.FirstOrDefault(e => e.PrefixLength != entry.PrefixLength && e.Overlaps(entry));
            if (clash != default)
            {
                warnings.Add(Finding.Warn(ReasonCodes.AllowedIpsOverlap, $"allowed_ips entry {entry} overlaps {clash} at a differing prefix length."));
                break;
            }
        }

        // REQ-VAL-031
        foreach (var entry in entries.Where(e => !Networks.IsWithin(e, subnets)))
            warnings.Add(Finding.Warn(ReasonCodes.AllowedIpsOutOfSubnet, $"allowed_ips entry {entry} lies outside every subnet of the interface."));

        // REQ-VAL-033
        if (spec.Endpoint is { } endpoint && TrySplitEndpoint(endpoint, out var host, out _) && !IsIPv4Literal(host))
            warnings.Add(Finding.Warn(ReasonCodes.EndpointNotIp, $"endpoint '{endpoint}' names a host; WireGuard keeps only the address it resolves to."));

        return warnings;
    }

    private static Finding? FirstError(PeerCheck check)
    {
        var spec = check.Spec;

        // REQ-VAL-011
        PublicKey? key = null;
        if (!check.GenerateKeypair && !Platform.PublicKey.TryParse(check.PublicKey, out key))
            return Finding.Error(ReasonCodes.PublicKeyInvalid, "public_key is not standard base64 of exactly 32 bytes.");

        // REQ-VAL-040
        if (key is not null && check.InterfacePublicKey is not null && key.Equals(check.InterfacePublicKey))
            return Finding.Error(ReasonCodes.PeerIsInterface, "The peer's public key is the interface's own public key.");

        // REQ-VAL-039
        if (spec.PresharedKey is { IsWellFormed: false })
            return Finding.Error(ReasonCodes.KeyInvalid, "preshared_key is not standard base64 of exactly 32 bytes.");

        // REQ-VAL-017, with the exception REQ-KEY-047 makes for a generated peer
        var texts = spec.AllowedIps ?? [];
        if (texts.Count == 0 && !check.GenerateKeypair)
            return Finding.Error(ReasonCodes.AllowedIpsRequired, "allowed_ips must hold at least one entry.");

        // REQ-VAL-020, REQ-VAL-047, REQ-VAL-041, REQ-VAL-044
        var entries = new List<Cidr>();
        foreach (var text in texts)
        {
            if (InterfaceRules.ParseEntry(text, "allowed_ips") is { } bad) return bad;
            Cidr.TryParse(text, out var entry);
            if (entry.HasHostBits)
                return Finding.Error(ReasonCodes.AllowedIpsNotCanonical, $"allowed_ips entry {text} has host bits set; {entry.Network} or {entry.Address}/32 is meant.");
            if (entry.PrefixLength == 0)
                return Finding.Error(ReasonCodes.AllowedIpsDefaultRoute, $"allowed_ips entry {text} is a default route, which would route the node's own traffic through the peer.");
            entries.Add(entry);
        }

        // REQ-VAL-012
        var taken = check.OtherPeers.SelectMany(p => Parsed(p.AllowedIps)).ToHashSet();
        foreach (var entry in entries)
            if (taken.Contains(entry))
                return Finding.Error(ReasonCodes.AllowedIpsDuplicate, $"allowed_ips entry {entry} is held by another peer of the interface.");

        // REQ-VAL-037
        if (spec.PersistentKeepalive is > 65535)
            return Finding.Error(ReasonCodes.KeepaliveInvalid, $"persistent_keepalive {spec.PersistentKeepalive} exceeds 65535.");
        if (check.ClientPersistentKeepalive is > 65535)
            return Finding.Error(ReasonCodes.KeepaliveInvalid, $"client_persistent_keepalive {check.ClientPersistentKeepalive} exceeds 65535.");

        // REQ-VAL-042, and REQ-VAL-020 for its host
        if (spec.Endpoint is { } endpoint)
        {
            if (!TrySplitEndpoint(endpoint, out var host, out _))
                return Finding.Error(ReasonCodes.EndpointInvalid, $"endpoint '{endpoint}' is not a host and a port from 1 to 65535.");
            if (host.Contains(':'))
                return Finding.Error(ReasonCodes.Ipv6NotSupported, $"endpoint '{endpoint}' is IPv6, and the agent is IPv4 only.");
        }

        if (check.GenerateKeypair)
        {
            var subnets = Networks.SubnetsOf(check.Interface.Addresses ?? []);
            if (entries.Count == 0)
            {
                // REQ-VAL-046
                var interfaceAddresses = (check.Interface.Addresses ?? []).Select(a => Cidr.TryParse(a, out var c) ? c : (Cidr?)null).OfType<Cidr>();
                if (subnets.Count == 0 || Networks.LowestFreeHost(subnets[0], interfaceAddresses, check.OtherPeers.SelectMany(p => Parsed(p.AllowedIps))) is null)
                    return Finding.Error(ReasonCodes.SubnetFull, "The interface's first subnet has no free host address for the peer.");
            }
            else if (!entries.Any(e => Networks.IsWithin(e, subnets)))
            {
                // REQ-VAL-043
                return Finding.Error(ReasonCodes.ClientAddressMissing, "No allowed_ips entry lies within the interface's subnets, so the client configuration would carry no address.");
            }
        }

        // REQ-VAL-020, REQ-VAL-047 for the client configuration's own fields
        foreach (var text in check.ClientAllowedIps ?? [])
            if (InterfaceRules.ParseEntry(text, "client_allowed_ips") is { } bad) return bad;
        foreach (var server in check.Dns ?? [])
            if (IPAddress.TryParse(server, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                return Finding.Error(ReasonCodes.Ipv6NotSupported, $"dns: '{server}' is IPv6, and the agent is IPv4 only.");

        return null;
    }

    /// <summary>Splits "host:port"; false when either part is missing or malformed (REQ-VAL-042).</summary>
    public static bool TrySplitEndpoint(string endpoint, out string host, out ushort port)
    {
        host = ""; port = 0;
        if (endpoint.StartsWith('['))
        {
            // A bracketed IPv6 literal: well-formed, refused by REQ-VAL-020.
            var close = endpoint.IndexOf("]:", StringComparison.Ordinal);
            if (close < 0 || !ushort.TryParse(endpoint[(close + 2)..], out port) || port == 0) return false;
            host = endpoint[1..close];
            return host.Contains(':');
        }
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0) return false;
        host = endpoint[..colon];
        var portText = endpoint[(colon + 1)..];
        if (!PortPattern().IsMatch(portText) || !ushort.TryParse(portText, out port) || port == 0) return false;
        if (host.Contains(':')) return true; // an unbracketed IPv6 literal, refused by REQ-VAL-020
        if (DigitsAndDots().IsMatch(host)) return IsIPv4Literal(host);
        return HostName().IsMatch(host);
    }

    private static bool IsIPv4Literal(string host) =>
        Cidr.TryParse(host + "/32", out var c) && !c.IsIPv6;

    private static IEnumerable<Cidr> Parsed(IReadOnlyList<string>? texts) =>
        (texts ?? []).Select(t => Cidr.TryParse(t, out var c) ? c : (Cidr?)null).OfType<Cidr>();

    [GeneratedRegex(@"^\d{1,5}$")]
    private static partial Regex PortPattern();

    [GeneratedRegex(@"^[\d.]+$")]
    private static partial Regex DigitsAndDots();

    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostName();
}

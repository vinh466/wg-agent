using System.Text.RegularExpressions;
using WgAgent.Core.Model;
using WgAgent.Platform;

namespace WgAgent.Core.Validation;

/// <summary>The interface-level rules of SPEC-07, in the order the module lists them.</summary>
public static partial class InterfaceRules
{
    public static bool IsValidName(string? name) =>
        name is not null && NamePattern().IsMatch(name) && name is not ("all" or "default");

    /// <param name="name">The interface the spec names.</param>
    /// <param name="spec">The spec, defaults applied.</param>
    /// <param name="creating">True for CreateInterface, which REQ-VAL-015 alone concerns.</param>
    /// <param name="host">What the host holds.</param>
    /// <param name="otherManaged">The specs of the agent's other interfaces, enabled or not.</param>
    public static ValidationResult Check(string name, InterfaceSpec spec, bool creating, HostView host,
        IReadOnlyDictionary<string, InterfaceSpec> otherManaged)
    {
        var error = FirstError(name, spec, creating, host, otherManaged);
        return new ValidationResult(error, error is null ? Warnings(spec) : []);
    }

    /// <summary>The warning rules of the interface (REQ-VAL-032), also read for status.warnings.</summary>
    public static IReadOnlyList<Finding> Warnings(InterfaceSpec spec)
    {
        var warnings = new List<Finding>();
        if (spec.Mtu is { } mtu && mtu is >= 68 and <= 65535 && mtu is < 1280 or > 1500)
            warnings.Add(Finding.Warn(ReasonCodes.MtuOutOfRange, $"MTU {mtu} lies outside the usual range of 1280 to 1500."));
        return warnings;
    }

    private static Finding? FirstError(string name, InterfaceSpec spec, bool creating, HostView host,
        IReadOnlyDictionary<string, InterfaceSpec> otherManaged)
    {
        // REQ-VAL-010
        if (!IsValidName(name))
            return Finding.Error(ReasonCodes.InterfaceNameInvalid,
                $"'{name}' is not a valid interface name: 1 to 15 letters, digits, '_' or '-', starting with a letter, and not 'all' or 'default'.");

        // REQ-VAL-015
        if (creating && (host.Links.Contains(name) || host.ConfigFiles.Contains(name)))
            return Finding.Error(ReasonCodes.InterfaceExists,
                $"'{name}' already exists on this host, as a network link or as /etc/wireguard/{name}.conf.");

        // REQ-VAL-039
        if (spec.PrivateKey is { IsWellFormed: false })
            return Finding.Error(ReasonCodes.KeyInvalid, "private_key is not standard base64 of exactly 32 bytes.");

        // REQ-VAL-016
        var addressTexts = spec.Addresses ?? [];
        if (addressTexts.Count == 0)
            return Finding.Error(ReasonCodes.AddressesRequired, "addresses must hold at least one address.");

        // REQ-VAL-020, REQ-VAL-047
        var addresses = new List<Cidr>();
        foreach (var text in addressTexts)
        {
            if (ParseEntry(text, "addresses") is { } bad) return bad;
            Cidr.TryParse(text, out var address);
            addresses.Add(address);
        }

        // REQ-VAL-036
        var port = spec.ListenPort ?? Defaults.ListenPort;
        if (port is 0 or > 65535)
            return Finding.Error(ReasonCodes.ListenPortInvalid, $"listen_port {port} lies outside 1 to 65535.");

        // REQ-VAL-013
        foreach (var (other, otherPort) in host.WireGuardListenPorts)
            if (other != name && otherPort == port)
                return Finding.Error(ReasonCodes.ListenPortInUse, $"Port {port} is held by the WireGuard interface '{other}'.");
        foreach (var (other, otherSpec) in otherManaged)
            if (other != name && otherSpec.ListenPort == port)
                return Finding.Error(ReasonCodes.ListenPortInUse, $"Port {port} belongs to the interface '{other}'.");

        // REQ-VAL-014
        var subnets = addresses.Select(a => a.Network).ToList();
        foreach (var (other, otherAddresses) in host.WireGuardAddresses)
        {
            if (other == name) continue;
            if (Clash(subnets, otherAddresses.Select(a => a.Network)) is { } clash)
                return Finding.Error(ReasonCodes.AddressConflict, $"{clash} overlaps an address of the WireGuard interface '{other}'.");
        }
        foreach (var (other, otherSpec) in otherManaged)
        {
            if (other == name) continue;
            if (Clash(subnets, Networks.SubnetsOf(otherSpec.Addresses ?? [])) is { } clash)
                return Finding.Error(ReasonCodes.AddressConflict, $"{clash} overlaps an address of the interface '{other}'.");
        }

        // REQ-VAL-038
        var mtu = spec.Mtu ?? Defaults.Mtu;
        if (mtu is < 68 or > 65535)
            return Finding.Error(ReasonCodes.MtuInvalid, $"mtu {mtu} lies outside 68 to 65535.");

        // REQ-VAL-045
        foreach (var command in (spec.PostUp ?? []).Concat(spec.PostDown ?? []))
            if (command.Contains('\n') || command.Contains('\r'))
                return Finding.Error(ReasonCodes.HookInvalid, "A post_up or post_down command contains a line break.");

        return null;
    }

    /// <summary>
    /// REQ-VAL-020 and REQ-VAL-047 for one entry of <paramref name="field"/>: null when the entry is an
    /// IPv4 address with a prefix length.
    /// </summary>
    internal static Finding? ParseEntry(string? text, string field)
    {
        if (Cidr.TryParse(text, out var cidr))
            return cidr.IsIPv6
                ? Finding.Error(ReasonCodes.Ipv6NotSupported, $"{field}: '{text}' is IPv6, and the overlay is IPv4 only.")
                : null;
        return text is not null && text.Contains(':')
            ? Finding.Error(ReasonCodes.Ipv6NotSupported, $"{field}: '{text}' is IPv6, and the overlay is IPv4 only.")
            : Finding.Error(ReasonCodes.CidrInvalid, $"{field}: '{text}' is not an address with a prefix length.");
    }

    private static Cidr? Clash(IEnumerable<Cidr> mine, IEnumerable<Cidr> theirs)
    {
        var theirList = theirs.ToList();
        foreach (var subnet in mine)
            if (theirList.Any(t => t.Overlaps(subnet)))
                return subnet;
        return null;
    }

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_-]{0,14}$")]
    private static partial Regex NamePattern();
}

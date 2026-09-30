using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace WgAgent.Platform;

/// <summary>
/// An address with a prefix length. An interface address keeps its host bits (10.8.0.1/24);
/// an allowed-IPs entry must carry none (REQ-VAL-041). Equality is by address and length.
/// </summary>
public readonly partial record struct Cidr(IPAddress Address, int PrefixLength)
{
    public bool IsIPv6 => Address.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>The network the address belongs to: host bits cleared.</summary>
    public Cidr Network => new(Mask(Address, PrefixLength), PrefixLength);

    public bool HasHostBits => !Address.Equals(Network.Address);

    /// <summary>True when this network contains <paramref name="other"/> entirely.</summary>
    public bool Contains(Cidr other) =>
        Address.AddressFamily == other.Address.AddressFamily
        && other.PrefixLength >= PrefixLength
        && Mask(other.Address, PrefixLength).Equals(Network.Address);

    /// <summary>Two networks overlap when one contains the other.</summary>
    public bool Overlaps(Cidr other) => Contains(other) || other.Contains(this);

    public override string ToString() => $"{Address}/{PrefixLength}";

    /// <summary>
    /// Parses "a.b.c.d/n" or an IPv6 "x::y/n". Shorthand IPv4 forms such as "10.1" or "0x0a",
    /// which <see cref="IPAddress.TryParse(string?, out IPAddress?)"/> would accept, are refused.
    /// </summary>
    public static bool TryParse(string? text, out Cidr cidr)
    {
        cidr = default;
        if (string.IsNullOrEmpty(text)) return false;
        var slash = text.IndexOf('/');
        if (slash <= 0 || slash == text.Length - 1 || text.IndexOf('/', slash + 1) >= 0) return false;

        var addressText = text[..slash];
        var prefixText = text[(slash + 1)..];
        if (!PrefixPattern().IsMatch(prefixText)) return false;
        if (!int.TryParse(prefixText, out var prefix)) return false;

        if (addressText.Contains(':'))
        {
            if (addressText.Contains('%')) return false;
            if (!IPAddress.TryParse(addressText, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6) return false;
            if (prefix > 128) return false;
            cidr = new Cidr(v6, prefix);
            return true;
        }

        if (!DottedQuad().IsMatch(addressText)) return false;
        if (!IPAddress.TryParse(addressText, out var v4) || v4.AddressFamily != AddressFamily.InterNetwork) return false;
        if (prefix > 32) return false;
        cidr = new Cidr(v4, prefix);
        return true;
    }

    /// <summary>The IPv4 address as a 32-bit number, for address arithmetic.</summary>
    public static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }

    public static IPAddress FromUInt32(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private static IPAddress Mask(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bitsInByte = Math.Clamp(prefixLength - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bitsInByte));
        }
        return new IPAddress(bytes);
    }

    [GeneratedRegex(@"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$")]
    private static partial Regex DottedQuad();

    [GeneratedRegex(@"^\d{1,3}$")]
    private static partial Regex PrefixPattern();
}

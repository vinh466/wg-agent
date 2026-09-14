using System.Net;
using System.Net.Sockets;

namespace WgAgent.Platform;

/// <summary>
/// An address with a prefix length, with the host bits kept.
/// </summary>
/// <remarks>
/// <para>
/// The host bits matter: <c>10.100.0.1/24</c> is the address to assign to an
/// interface, and masking it to <c>10.100.0.0/24</c> would put the network
/// address there instead. <see cref="System.Net.IPNetwork"/> refuses a value
/// with host bits set, so it cannot represent an interface address at all.
/// </para>
/// <para>
/// <c>allowed_ips</c> and routes are the other case: the WireGuard device and
/// the routing table both normalise those, so a comparison has to be made
/// against <see cref="Masked"/> or the value reads back different from what was
/// written and drifts on every reconcile pass.
/// </para>
/// </remarks>
public readonly record struct Cidr(IPAddress Address, int PrefixLength)
{
    public bool IsIPv4 => Address.AddressFamily == AddressFamily.InterNetwork;
    public bool IsIPv6 => Address.AddressFamily == AddressFamily.InterNetworkV6;

    public int MaxPrefixLength => IsIPv4 ? 32 : 128;

    public static Cidr Parse(string value)
    {
        if (!TryParse(value, out var cidr, out string? error))
            throw new FormatException(error);
        return cidr;
    }

    public static bool TryParse(string? value, out Cidr cidr, out string? error)
    {
        cidr = default;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "the value is empty";
            return false;
        }

        int slash = value.LastIndexOf('/');
        if (slash < 0)
        {
            error = $"\"{value}\" has no prefix length";
            return false;
        }

        if (!IPAddress.TryParse(value[..slash], out var addr))
        {
            error = $"\"{value[..slash]}\" is not an IP address";
            return false;
        }
        if (!int.TryParse(value[(slash + 1)..], out int bits))
        {
            error = $"\"{value[(slash + 1)..]}\" is not a prefix length";
            return false;
        }

        int max = addr.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (bits < 0 || bits > max)
        {
            error = $"prefix length {bits} is outside 0 to {max}";
            return false;
        }

        cidr = new Cidr(addr, bits);
        return true;
    }

    /// <summary>Clears the host bits, giving the network this address sits in.</summary>
    public Cidr Masked()
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!Address.TryWriteBytes(bytes, out int written)) return this;

        for (int i = 0; i < written; i++)
        {
            int keep = PrefixLength - i * 8;
            bytes[i] = keep >= 8 ? bytes[i]
                     : keep <= 0 ? (byte)0
                     : (byte)(bytes[i] & (0xFF << (8 - keep)));
        }
        return new Cidr(new IPAddress(bytes[..written]), PrefixLength);
    }

    /// <summary>Whether the two ranges share any address.</summary>
    public bool Overlaps(Cidr other)
    {
        if (Address.AddressFamily != other.Address.AddressFamily) return false;

        int shared = Math.Min(PrefixLength, other.PrefixLength);
        Span<byte> a = stackalloc byte[16];
        Span<byte> b = stackalloc byte[16];
        Address.TryWriteBytes(a, out int lenA);
        other.Address.TryWriteBytes(b, out _);

        for (int i = 0; i < lenA && shared > 0; i++, shared -= 8)
        {
            int mask = shared >= 8 ? 0xFF : 0xFF << (8 - shared);
            if ((a[i] & mask) != (b[i] & mask)) return false;
        }
        return true;
    }

    /// <summary>Whether <paramref name="other"/> lies wholly inside this range.</summary>
    public bool Contains(Cidr other)
        => other.PrefixLength >= PrefixLength && Overlaps(other);

    public override string ToString() => $"{Address}/{PrefixLength}";
}

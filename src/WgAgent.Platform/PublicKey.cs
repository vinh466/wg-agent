namespace WgAgent.Platform;

/// <summary>
/// A WireGuard public key: 32 bytes. Not sensitive. Standard base64 with padding everywhere
/// (REQ-RES-027), unpadded base64url in a REST path (REQ-RES-021).
/// </summary>
public sealed class PublicKey : IEquatable<PublicKey>
{
    public const int Length = 32;
    private readonly byte[] _bytes;

    private PublicKey(byte[] bytes) => _bytes = bytes;

    /// <summary>Parses standard base64 of exactly 32 bytes (REQ-VAL-011).</summary>
    public static bool TryParse(string? base64, out PublicKey key)
    {
        key = null!;
        if (!KeyBytes.TryDecode(base64, out var bytes)) return false;
        key = new PublicKey(bytes);
        return true;
    }

    public static PublicKey FromBytes(ReadOnlySpan<byte> bytes) =>
        bytes.Length == Length ? new PublicKey(bytes.ToArray()) : throw new ArgumentException("A key is 32 bytes.", nameof(bytes));

    /// <summary>Decodes the unpadded base64url form a REST path carries (REQ-RES-021).</summary>
    public static bool TryParsePathSegment(string? segment, out PublicKey key)
    {
        key = null!;
        if (string.IsNullOrEmpty(segment) || segment.Length != 43) return false;
        if (segment.IndexOfAny(['+', '/', '=']) >= 0) return false;
        var standard = segment.Replace('-', '+').Replace('_', '/') + "=";
        return TryParse(standard, out key);
    }

    /// <summary>The unpadded base64url form for a REST path (REQ-RES-021).</summary>
    public string ToPathSegment() => Convert.ToBase64String(_bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Standard base64 with padding (REQ-RES-027).</summary>
    public override string ToString() => Convert.ToBase64String(_bytes);

    public bool Equals(PublicKey? other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);
    public override bool Equals(object? obj) => obj is PublicKey other && Equals(other);
    public override int GetHashCode() => BitConverter.ToInt32(_bytes, 0);
}

internal static class KeyBytes
{
    /// <summary>
    /// Standard base64, padded, decoding to exactly 32 bytes — and the one spelling of them: a string
    /// whose unused final bits are set is refused, as WireGuard's tools refuse it (REQ-VAL-011).
    /// </summary>
    public static bool TryDecode(string? base64, out byte[] bytes)
    {
        bytes = [];
        if (base64 is null || base64.Length != 44 || !base64.EndsWith('=') || base64.EndsWith("==")) return false;
        var buffer = new byte[33];
        if (!Convert.TryFromBase64String(base64, buffer, out var written) || written != PublicKey.Length) return false;
        if (Convert.ToBase64String(buffer, 0, PublicKey.Length) != base64) return false;
        bytes = buffer[..PublicKey.Length];
        return true;
    }
}

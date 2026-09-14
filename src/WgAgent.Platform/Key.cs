using System.Security.Cryptography;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace WgAgent.Platform;

/// <summary>
/// A WireGuard key. <see cref="ToString"/> redacts, so a key cannot reach a log
/// line, an audit record or an API response through ordinary formatting —
/// REQ-SEC-050. Reaching the value is deliberately explicit.
/// </summary>
public readonly struct Key : IEquatable<Key>
{
    public const int Size = 32;

    private readonly byte[]? _bytes;

    private Key(byte[] bytes) => _bytes = bytes;

    /// <summary>An absent key. The kernel reports one as 32 zero bytes.</summary>
    public static Key None => default;

    /// <summary>
    /// Wraps 32 bytes. Anything else, and 32 zero bytes, yield an absent key.
    /// </summary>
    /// <remarks>
    /// All-zero is absent because that is the kernel's own convention: a device
    /// without a private key and a peer without a preshared key both read back
    /// as zero, and writing zero is how a preshared key is cleared. Holding the
    /// rule here rather than in each adapter is what keeps the fake and the
    /// netlink adapter from disagreeing about what an unset key looks like.
    /// </remarks>
    public static Key FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size) return None;

        bool allZero = true;
        foreach (byte b in bytes)
        {
            if (b != 0) { allZero = false; break; }
        }
        if (allZero) return None;

        return new Key(bytes.ToArray());
    }

    /// <summary>
    /// Parses the base64 form. This is the one decoder, so a value that
    /// round-trips through the store or the contract meets the same rule at
    /// both ends — REQ-RES-027 fixes standard base64 with padding outside the
    /// REST paths.
    /// </summary>
    public static Key Parse(string base64)
    {
        if (!TryParse(base64, out var key, out string? error))
            throw new FormatException(error);
        return key;
    }

    public static bool TryParse(string? base64, out Key key, out string? error)
    {
        key = None;
        error = null;

        if (string.IsNullOrEmpty(base64))
        {
            error = "the key is empty";
            return false;
        }

        Span<byte> buf = stackalloc byte[Size + 1];
        if (!Convert.TryFromBase64String(base64, buf, out int written))
        {
            error = $"\"{base64}\" is not base64";
            return false;
        }
        if (written != Size)
        {
            error = $"the key is {written} bytes, want {Size}";
            return false;
        }

        key = FromBytes(buf[..Size]);
        return true;
    }

    /// <summary>
    /// Whether a key is set. It is the only thing a diagnostic may disclose
    /// about one — REQ-DIA-047.
    /// </summary>
    public bool IsPresent => _bytes is not null;

    /// <summary>
    /// The key itself. Callers are the store and the client-config generator;
    /// no response path may use it.
    /// </summary>
    public string ToBase64() => _bytes is null ? "" : Convert.ToBase64String(_bytes);

    /// <summary>
    /// The raw bytes, for the adapter that has to put them on a netlink socket.
    /// Returns 32 zero bytes for an absent key, which is how the kernel is told
    /// to clear one.
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"need {Size} bytes", nameof(destination));

        if (_bytes is null) destination[..Size].Clear();
        else _bytes.CopyTo(destination);
    }

    /// <summary>
    /// Derives the public half of a private key.
    /// </summary>
    /// <remarks>
    /// It lives here because the derivation is a property of a WireGuard key
    /// rather than of any one caller: the netlink adapter reads a public key
    /// back from the kernel, and the fake has to produce the same value for the
    /// same private key or a test above the port would see a device the kernel
    /// could not be in.
    /// </remarks>
    public Key GetPublicKey()
    {
        if (_bytes is null)
            throw new InvalidOperationException("no private key");

        // No .NET release through 10 exposes X25519: it is absent from
        // ECCurve.NamedCurves, the OID 1.3.101.110 is rejected, and the PKCS#8
        // importer does not know it. .NET 10 added the post-quantum set —
        // MLKem, MLDsa, SlhDsa — and not this curve. BouncyCastle is pure
        // managed, so the single-file build survives, and the AOT trimmer keeps
        // only the curve.
        byte[] pub = new byte[Size];
        X25519.ScalarMultBase(_bytes, 0, pub, 0);
        return FromBytes(pub);
    }

    /// <summary>
    /// Generates a private key from a cryptographically secure source —
    /// REQ-KEY-001 for an interface, REQ-KEY-011 for a peer.
    /// </summary>
    /// <remarks>
    /// The clamping is Curve25519's. The kernel applies it on receipt in any
    /// case, and applying it here means the value stored is the value in
    /// effect, so a public key derived from the stored private key matches the
    /// one the device reports back.
    /// </remarks>
    public static Key Generate()
    {
        Span<byte> b = stackalloc byte[Size];
        RandomNumberGenerator.Fill(b);
        b[0] &= 248;
        b[31] &= 127;
        b[31] |= 64;
        return FromBytes(b);
    }

    /// <summary>Redacts. Use <see cref="ToBase64"/> when the value is genuinely needed.</summary>
    public override string ToString() => _bytes is null ? "" : "[redacted]";

    public bool Equals(Key other)
    {
        if (_bytes is null || other._bytes is null) return _bytes is null && other._bytes is null;
        return CryptographicOperations.FixedTimeEquals(_bytes, other._bytes);
    }

    public override bool Equals(object? obj) => obj is Key k && Equals(k);

    // Deliberately weak: a key must not be a dictionary discriminator, and a
    // strong hash of a secret is one more place the value leaks from.
    public override int GetHashCode() => _bytes is null ? 0 : 1;

    public static bool operator ==(Key a, Key b) => a.Equals(b);
    public static bool operator !=(Key a, Key b) => !a.Equals(b);
}

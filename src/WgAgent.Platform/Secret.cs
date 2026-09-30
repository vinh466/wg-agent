using System.Security.Cryptography;
using System.Text;

namespace WgAgent.Platform;

/// <summary>
/// An opaque secret string — the API token of ADR-0015. Every textual form is <c>[REDACTED]</c>
/// (REQ-SEC-050); the value leaves only through <see cref="Reveal"/> or the constant-time
/// <see cref="Matches"/> (REQ-SEC-073), so it never reaches a log or a response (REQ-SEC-076).
/// </summary>
public sealed class Secret
{
    public const string Redacted = "[REDACTED]";
    private readonly byte[] _utf8;

    private Secret(byte[] utf8) => _utf8 = utf8;

    public static Secret From(string value) => new(Encoding.UTF8.GetBytes(value));

    /// <summary>Constant-time comparison against a candidate, so a mismatch leaks no timing (REQ-SEC-073).</summary>
    public bool Matches(string candidate) =>
        CryptographicOperations.FixedTimeEquals(_utf8, Encoding.UTF8.GetBytes(candidate));

    /// <summary>The value itself. Call sites are few and deliberate: the file writer and the compare.</summary>
    public string Reveal() => Encoding.UTF8.GetString(_utf8);

    public override string ToString() => Redacted;
}

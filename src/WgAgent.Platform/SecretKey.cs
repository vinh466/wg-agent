using System.Text.Json;
using System.Text.Json.Serialization;

namespace WgAgent.Platform;

/// <summary>
/// A private or preshared key. Every textual and serialised form of the value is
/// <c>[REDACTED]</c> (REQ-SEC-050); the one way to the key itself is <see cref="Reveal"/>, which
/// only the store, the renderer and the standard input of <c>wg</c> call.
/// A malformed input is kept as a malformed key rather than rejected by the parser, so that
/// validation reports it with its own reason code (REQ-VAL-039) and the text is never retained.
/// </summary>
[JsonConverter(typeof(SecretKeyJsonConverter))]
public sealed class SecretKey
{
    public const string Redacted = "[REDACTED]";
    private readonly byte[]? _bytes;

    private SecretKey(byte[]? bytes) => _bytes = bytes;

    public static SecretKey Malformed { get; } = new(null);

    public bool IsWellFormed => _bytes is not null;

    public static SecretKey Parse(string? base64) =>
        KeyBytes.TryDecode(base64, out var bytes) ? new SecretKey(bytes) : Malformed;

    public static SecretKey FromBytes(ReadOnlySpan<byte> bytes) =>
        bytes.Length == PublicKey.Length ? new SecretKey(bytes.ToArray()) : throw new ArgumentException("A key is 32 bytes.", nameof(bytes));

    /// <summary>The key in standard base64. Call sites are few and deliberate.</summary>
    public string Reveal() =>
        _bytes is null ? throw new InvalidOperationException("A malformed key has no value.") : Convert.ToBase64String(_bytes);

    public override string ToString() => Redacted;
}

/// <summary>Reads a key from its base64 text; writes <c>[REDACTED]</c>, whatever the key.</summary>
public sealed class SecretKeyJsonConverter : JsonConverter<SecretKey>
{
    public override SecretKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? SecretKey.Parse(reader.GetString()) : SecretKey.Malformed;

    public override void Write(Utf8JsonWriter writer, SecretKey value, JsonSerializerOptions options) =>
        writer.WriteStringValue(SecretKey.Redacted);
}

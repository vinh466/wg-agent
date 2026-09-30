using System.Text.Json.Serialization;

namespace WgAgent.Core.Model;

/// <summary>REQ-RES-035.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OperState>))]
public enum OperState
{
    [JsonStringEnumMemberName("UP")] Up,
    [JsonStringEnumMemberName("DOWN")] Down,
    [JsonStringEnumMemberName("ABSENT")] Absent,
}

/// <summary>One validation finding of warning severity (REQ-RES-033, REQ-VAL-002).</summary>
public sealed record Warning(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>SPEC-01 section 3.3, as far as the first release reports it.</summary>
public sealed record InterfaceStatus
{
    [JsonPropertyName("public_key")] public string? PublicKey { get; init; }
    [JsonPropertyName("listen_port")] public uint? ListenPort { get; init; }
    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; init; }
    [JsonPropertyName("oper_state")] public OperState OperState { get; init; }
    [JsonPropertyName("peer_count")] public int? PeerCount { get; init; }
    [JsonPropertyName("warnings")] public IReadOnlyList<Warning> Warnings { get; init; } = [];
}

/// <summary>SPEC-01 section 4.3, as far as the first release reports it.</summary>
public sealed record PeerStatus
{
    [JsonPropertyName("last_handshake_at")] public DateTimeOffset? LastHandshakeAt { get; init; }
    [JsonPropertyName("handshake_age_seconds")] public long? HandshakeAgeSeconds { get; init; }
    [JsonPropertyName("online")] public bool Online { get; init; }
    [JsonPropertyName("rx_bytes")] public long? RxBytes { get; init; }
    [JsonPropertyName("tx_bytes")] public long? TxBytes { get; init; }
    [JsonPropertyName("resolved_endpoint")] public string? ResolvedEndpoint { get; init; }
    [JsonPropertyName("warnings")] public IReadOnlyList<Warning> Warnings { get; init; } = [];

    /// <summary>
    /// REQ-RES-025: online when a handshake has happened and is younger than the threshold.
    /// </summary>
    public static bool IsOnline(DateTimeOffset? lastHandshakeAt, DateTimeOffset now, TimeSpan threshold) =>
        lastHandshakeAt is { } at && now - at < threshold;
}

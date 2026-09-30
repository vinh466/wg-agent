using System.Text.Json.Serialization;
using WgAgent.Platform;

namespace WgAgent.Core.Model;

/// <summary>
/// The desired state of a peer — SPEC-01 section 4.2. The interface name and the public key
/// identify the resource and are not members (REQ-RES-020, REQ-RES-034).
/// </summary>
public sealed record PeerSpec
{
    [JsonPropertyName("preshared_key"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecretKey? PresharedKey { get; init; }

    [JsonPropertyName("allowed_ips")] public IReadOnlyList<string>? AllowedIps { get; init; }

    [JsonPropertyName("endpoint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Endpoint { get; init; }

    [JsonPropertyName("persistent_keepalive")] public uint? PersistentKeepalive { get; init; }
    [JsonPropertyName("labels")] public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

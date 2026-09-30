using System.Text.Json.Serialization;

namespace WgAgent.Core.Store;

// The on-disk form. Keys are plain text here and nowhere else: SecretKey redacts every form of
// itself (REQ-SEC-050), and the store is the one place a key must survive a restart.

internal sealed class StoreFile
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
    [JsonPropertyName("interfaces")] public Dictionary<string, StoreInterface> Interfaces { get; set; } = [];
}

internal sealed class StoreInterface
{
    [JsonPropertyName("spec")] public StoreInterfaceSpec Spec { get; set; } = new();
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("peers")] public Dictionary<string, StorePeerSpec> Peers { get; set; } = [];
}

internal sealed class StoreInterfaceSpec
{
    [JsonPropertyName("private_key")] public string? PrivateKey { get; set; }
    [JsonPropertyName("listen_port")] public uint ListenPort { get; set; }
    [JsonPropertyName("addresses")] public List<string> Addresses { get; set; } = [];
    [JsonPropertyName("mtu")] public uint Mtu { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("labels")] public Dictionary<string, string> Labels { get; set; } = [];
    [JsonPropertyName("post_up")] public List<string> PostUp { get; set; } = [];
    [JsonPropertyName("post_down")] public List<string> PostDown { get; set; } = [];
}

internal sealed class StorePeerSpec
{
    [JsonPropertyName("preshared_key")] public string? PresharedKey { get; set; }
    [JsonPropertyName("allowed_ips")] public List<string> AllowedIps { get; set; } = [];
    [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
    [JsonPropertyName("persistent_keepalive")] public uint PersistentKeepalive { get; set; }
    [JsonPropertyName("labels")] public Dictionary<string, string> Labels { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StoreFile))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;

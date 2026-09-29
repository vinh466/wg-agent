using System.Text.Json.Serialization;
using WgAgent.Core.Model;

namespace WgAgent.Core.Store;

// The on-disk shape of desired state. Its contents are bounded by REQ-RCN-002:
// spec values, instance_id, created_at, deletion records and adoption records.
// REQ-RCN-050 keeps status and traffic counters out.
//
// A single JSON file written atomically. bbolt is named in SPEC-03 without an
// RFC 2119 keyword, so it is explanatory rather than required, and REQ-RCN-001
// to REQ-RCN-005 are met by a file written to a temporary path and renamed at
// mode 0600.

internal sealed class StoreFile
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; }

    [JsonPropertyName("interfaces")]
    public Dictionary<string, IfaceRecord>? Interfaces { get; set; }

    [JsonPropertyName("deletions")]
    public Dictionary<string, DeletionRecord>? Deletions { get; set; }

    [JsonPropertyName("adoptions")]
    public Dictionary<string, AdoptionRecord>? Adoptions { get; set; }

    [JsonPropertyName("peers")]
    public Dictionary<string, List<Peer>>? Peers { get; set; }

    /// <summary>
    /// A shallow copy of every map, so a failed transaction leaves the in-memory
    /// state untouched as well as the file. Values are never mutated in place —
    /// a mutation replaces a whole map entry — so copying the references is safe.
    /// </summary>
    public StoreFile Clone() => new()
    {
        Schema = Schema,
        Interfaces = Interfaces is null ? null : new Dictionary<string, IfaceRecord>(Interfaces),
        Deletions = Deletions is null ? null : new Dictionary<string, DeletionRecord>(Deletions),
        Adoptions = Adoptions is null ? null : new Dictionary<string, AdoptionRecord>(Adoptions),
        Peers = Peers is null ? null : new Dictionary<string, List<Peer>>(Peers),
    };
}

internal sealed class IfaceRecord
{
    [JsonPropertyName("instance_id")]
    public string InstanceId { get; set; } = "";

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("spec")]
    public InterfaceSpec Spec { get; set; } = new();
}

internal sealed class DeletionRecord
{
    [JsonPropertyName("recorded_at")]
    public string RecordedAt { get; set; } = "";
}

/// <summary>
/// The record REQ-RCN-070 requires. It names the interface by being keyed on it,
/// and holds the forwarding sysctl value REQ-FWD-024 restores.
/// </summary>
internal sealed class AdoptionRecord
{
    [JsonPropertyName("adopted_at")]
    public string AdoptedAt { get; set; } = "";

    [JsonPropertyName("forwarding_baseline")]
    public string ForwardingBaseline { get; set; } = "";
}

// Source-generated serialization: NativeAOT rejects the reflection-based
// serializer, so the shape is declared to the generator here.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StoreFile))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;

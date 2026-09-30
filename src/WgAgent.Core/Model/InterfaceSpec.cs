using System.Text.Json.Serialization;
using WgAgent.Platform;

namespace WgAgent.Core.Model;

/// <summary>
/// The desired state of an interface — SPEC-01 section 3.2. An omitted field is null until
/// <see cref="Defaults.Apply(InterfaceSpec)"/> resolves it; the store holds resolved values.
/// The name is not a member: it identifies the resource (REQ-RES-034). No peer list either
/// (REQ-RES-030).
/// </summary>
public sealed record InterfaceSpec
{
    [JsonPropertyName("private_key"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecretKey? PrivateKey { get; init; }

    [JsonPropertyName("listen_port")] public uint? ListenPort { get; init; }
    [JsonPropertyName("addresses")] public IReadOnlyList<string>? Addresses { get; init; }
    [JsonPropertyName("mtu")] public uint? Mtu { get; init; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; init; }
    [JsonPropertyName("labels")] public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>Set through the CLI alone (REQ-CLI-024); never in an API response (REQ-API-084).</summary>
    [JsonPropertyName("post_up")] public IReadOnlyList<string>? PostUp { get; init; }

    [JsonPropertyName("post_down")] public IReadOnlyList<string>? PostDown { get; init; }
}

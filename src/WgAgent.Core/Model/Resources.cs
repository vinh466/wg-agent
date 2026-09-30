using System.Text.Json.Serialization;

namespace WgAgent.Core.Model;

/// <summary>
/// An interface as a caller sees it: identity outside spec and status (REQ-RES-034), spec
/// separate from status (REQ-RES-001).
/// </summary>
public sealed record InterfaceResource
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("spec")] public InterfaceSpec? Spec { get; init; }
    [JsonPropertyName("status")] public required InterfaceStatus Status { get; init; }
}

/// <summary>A peer, identified by its interface and public key (REQ-RES-020).</summary>
public sealed record PeerResource
{
    [JsonPropertyName("interface_name")] public required string InterfaceName { get; init; }
    [JsonPropertyName("public_key")] public required string PublicKey { get; init; }
    [JsonPropertyName("spec")] public required PeerSpec Spec { get; init; }
    [JsonPropertyName("status")] public required PeerStatus Status { get; init; }
}

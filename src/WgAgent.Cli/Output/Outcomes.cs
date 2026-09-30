using System.Text.Json.Serialization;
using WgAgent.Core.Model;

namespace WgAgent.Cli.Output;

/// <summary>A write's outcome under <c>--output json</c> (REQ-CLI-031).</summary>
public sealed record InterfaceOutcome
{
    [JsonPropertyName("interface")] public required InterfaceResource Interface { get; init; }
    [JsonPropertyName("restarted")] public required bool Restarted { get; init; }
}

/// <summary>
/// A peer write's outcome under <c>--output json</c> (REQ-CLI-031). The generated keys are plain
/// strings here, deliberately: this is the one response that returns them (REQ-KEY-011, REQ-KEY-021).
/// </summary>
public sealed record PeerOutcome
{
    [JsonPropertyName("peer")] public required PeerResource Peer { get; init; }
    [JsonPropertyName("restarted")] public required bool Restarted { get; init; }

    [JsonPropertyName("private_key"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrivateKey { get; init; }

    [JsonPropertyName("preshared_key"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PresharedKey { get; init; }

    [JsonPropertyName("client_configuration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientConfiguration { get; init; }
}

/// <summary>The token `token rotate` generated, its one printing (REQ-CLI-011).</summary>
public sealed record TokenInfo
{
    [JsonPropertyName("token")] public required string Token { get; init; }
}

/// <summary>REQ-CLI-033</summary>
public sealed record VersionInfo
{
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("commit")] public required string Commit { get; init; }
}

/// <summary>The document of REQ-CLI-025, in the form of REQ-CLI-034.</summary>
public sealed record InterfaceDocument
{
    [JsonPropertyName("spec")] public required InterfaceSpec Spec { get; init; }
    /// <summary>Absent when the interface has no peers yet.</summary>
    [JsonPropertyName("peers")] public IReadOnlyList<PeerDocument>? Peers { get; init; }
}

public sealed record PeerDocument
{
    [JsonPropertyName("public_key")] public required string PublicKey { get; init; }
    [JsonPropertyName("spec")] public required PeerSpec Spec { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(InterfaceOutcome))]
[JsonSerializable(typeof(PeerOutcome))]
[JsonSerializable(typeof(TokenInfo))]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(InterfaceDocument))]
public sealed partial class CliJsonContext : JsonSerializerContext;

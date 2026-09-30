using System.Text.Json.Serialization;
using WgAgent.Core.Model;
using WgAgent.Platform;
using WgAgent.Service;

namespace WgAgent.Api;

// The request and response bodies of api/openapi.yaml. Requests disallow an unmapped member so a
// misspelling is FIELD_UNKNOWN, not a silent default (REQ-VAL-050). Responses hide the write-only
// keys (REQ-RES-013, REQ-RES-022) and the CLI-only hooks (REQ-API-084) by simply not carrying them;
// the readable status types of the model are reused as they are.

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateInterfaceRequest
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("private_key")] public string? PrivateKey { get; init; }
    [JsonPropertyName("listen_port")] public uint? ListenPort { get; init; }
    [JsonPropertyName("addresses")] public IReadOnlyList<string>? Addresses { get; init; }
    [JsonPropertyName("mtu")] public uint? Mtu { get; init; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; init; }
    [JsonPropertyName("labels")] public IReadOnlyDictionary<string, string>? Labels { get; init; }

    public InterfaceSpec ToSpec() => new()
    {
        PrivateKey = PrivateKey is null ? null : SecretKey.Parse(PrivateKey),
        ListenPort = ListenPort,
        Addresses = Addresses,
        Mtu = Mtu,
        Enabled = Enabled,
        Labels = Labels,
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateInterfaceRequest
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("private_key")] public string? PrivateKey { get; init; }
    [JsonPropertyName("listen_port")] public uint? ListenPort { get; init; }
    [JsonPropertyName("addresses")] public IReadOnlyList<string>? Addresses { get; init; }
    [JsonPropertyName("mtu")] public uint? Mtu { get; init; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; init; }
    [JsonPropertyName("labels")] public IReadOnlyDictionary<string, string>? Labels { get; init; }

    public InterfaceSpec ToSpec() => new()
    {
        PrivateKey = PrivateKey is null ? null : SecretKey.Parse(PrivateKey),
        ListenPort = ListenPort,
        Addresses = Addresses,
        Mtu = Mtu,
        Enabled = Enabled,
        Labels = Labels,
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreatePeerRequest
{
    [JsonPropertyName("public_key")] public string? PublicKey { get; init; }
    [JsonPropertyName("generate_keypair")] public bool GenerateKeypair { get; init; }
    [JsonPropertyName("generate_preshared_key")] public bool GeneratePresharedKey { get; init; }
    [JsonPropertyName("preshared_key")] public string? PresharedKey { get; init; }
    [JsonPropertyName("allowed_ips")] public IReadOnlyList<string>? AllowedIps { get; init; }
    [JsonPropertyName("endpoint")] public string? Endpoint { get; init; }
    [JsonPropertyName("persistent_keepalive")] public uint? PersistentKeepalive { get; init; }
    [JsonPropertyName("labels")] public IReadOnlyDictionary<string, string>? Labels { get; init; }
    [JsonPropertyName("client_allowed_ips")] public IReadOnlyList<string>? ClientAllowedIps { get; init; }
    [JsonPropertyName("client_persistent_keepalive")] public uint? ClientPersistentKeepalive { get; init; }
    [JsonPropertyName("dns")] public IReadOnlyList<string>? Dns { get; init; }
    [JsonPropertyName("node_endpoint")] public string? NodeEndpoint { get; init; }

    public Service.CreatePeerRequest ToServiceRequest() => new()
    {
        PublicKey = PublicKey,
        GenerateKeypair = GenerateKeypair,
        GeneratePresharedKey = GeneratePresharedKey,
        Spec = new PeerSpec
        {
            PresharedKey = PresharedKey is null ? null : SecretKey.Parse(PresharedKey),
            AllowedIps = AllowedIps,
            Endpoint = Endpoint,
            PersistentKeepalive = PersistentKeepalive,
            Labels = Labels,
        },
        ClientAllowedIps = ClientAllowedIps,
        ClientPersistentKeepalive = ClientPersistentKeepalive,
        Dns = Dns,
        NodeEndpoint = NodeEndpoint,
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdatePeerRequest
{
    [JsonPropertyName("public_key")] public string? PublicKey { get; init; }
    [JsonPropertyName("preshared_key")] public string? PresharedKey { get; init; }
    [JsonPropertyName("allowed_ips")] public IReadOnlyList<string>? AllowedIps { get; init; }
    [JsonPropertyName("endpoint")] public string? Endpoint { get; init; }
    [JsonPropertyName("persistent_keepalive")] public uint? PersistentKeepalive { get; init; }
    [JsonPropertyName("labels")] public IReadOnlyDictionary<string, string>? Labels { get; init; }

    public PeerSpec ToSpec() => new()
    {
        PresharedKey = PresharedKey is null ? null : SecretKey.Parse(PresharedKey),
        AllowedIps = AllowedIps,
        Endpoint = Endpoint,
        PersistentKeepalive = PersistentKeepalive,
        Labels = Labels,
    };
}

/// <summary>An interface's readable spec — no private_key (REQ-RES-013), no hooks (REQ-API-084).</summary>
public sealed record ApiInterfaceSpec
{
    [JsonPropertyName("listen_port")] public uint? ListenPort { get; init; }
    [JsonPropertyName("addresses")] public IReadOnlyList<string>? Addresses { get; init; }
    [JsonPropertyName("mtu")] public uint? Mtu { get; init; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; init; }
    [JsonPropertyName("labels")] public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

public sealed record ApiInterface
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("spec")] public required ApiInterfaceSpec Spec { get; init; }
    [JsonPropertyName("status")] public required InterfaceStatus Status { get; init; }

    public static ApiInterface From(InterfaceResource r) => new()
    {
        Name = r.Name,
        Spec = new ApiInterfaceSpec
        {
            ListenPort = r.Spec!.ListenPort,
            Addresses = r.Spec.Addresses,
            Mtu = r.Spec.Mtu,
            Enabled = r.Spec.Enabled,
            Labels = r.Spec.Labels,
        },
        Status = r.Status,
    };
}

/// <summary>A peer's readable spec — no preshared_key (REQ-RES-022).</summary>
public sealed record ApiPeerSpec
{
    [JsonPropertyName("allowed_ips")] public IReadOnlyList<string>? AllowedIps { get; init; }
    [JsonPropertyName("endpoint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Endpoint { get; init; }
    [JsonPropertyName("persistent_keepalive")] public uint? PersistentKeepalive { get; init; }
    [JsonPropertyName("labels")] public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

public sealed record ApiPeer
{
    [JsonPropertyName("interface_name")] public required string InterfaceName { get; init; }
    [JsonPropertyName("public_key")] public required string PublicKey { get; init; }
    [JsonPropertyName("spec")] public required ApiPeerSpec Spec { get; init; }
    [JsonPropertyName("status")] public required PeerStatus Status { get; init; }

    public static ApiPeer From(PeerResource r) => new()
    {
        InterfaceName = r.InterfaceName,
        PublicKey = r.PublicKey,
        Spec = new ApiPeerSpec
        {
            AllowedIps = r.Spec.AllowedIps,
            Endpoint = r.Spec.Endpoint,
            PersistentKeepalive = r.Spec.PersistentKeepalive,
            Labels = r.Spec.Labels,
        },
        Status = r.Status,
    };
}

public sealed record InterfaceWriteResult
{
    [JsonPropertyName("interface")] public required ApiInterface Interface { get; init; }
    [JsonPropertyName("restarted")] public required bool Restarted { get; init; }
}

public sealed record PeerWriteResult
{
    [JsonPropertyName("peer")] public required ApiPeer Peer { get; init; }
    [JsonPropertyName("restarted")] public required bool Restarted { get; init; }
}

public sealed record CreatePeerResult
{
    [JsonPropertyName("peer")] public required ApiPeer Peer { get; init; }
    [JsonPropertyName("restarted")] public required bool Restarted { get; init; }
    [JsonPropertyName("private_key"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrivateKey { get; init; }
    [JsonPropertyName("preshared_key"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PresharedKey { get; init; }
    [JsonPropertyName("client_configuration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientConfiguration { get; init; }
}

public sealed record VersionResponse
{
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("commit")] public required string Commit { get; init; }
    [JsonPropertyName("started_at")] public required DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("uptime_seconds")] public required long UptimeSeconds { get; init; }
}

public sealed record HealthResponse
{
    [JsonPropertyName("status")] public required string Status { get; init; }
}

/// <summary>An RFC 9457 problem document carrying its reason code (REQ-API-082).</summary>
public sealed record Problem
{
    [JsonPropertyName("type")] public string Type { get; init; } = "about:blank";
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("status")] public required int Status { get; init; }
    [JsonPropertyName("detail")] public required string Detail { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(CreateInterfaceRequest))]
[JsonSerializable(typeof(UpdateInterfaceRequest))]
[JsonSerializable(typeof(CreatePeerRequest))]
[JsonSerializable(typeof(UpdatePeerRequest))]
[JsonSerializable(typeof(ApiInterface))]
[JsonSerializable(typeof(IReadOnlyList<ApiInterface>))]
[JsonSerializable(typeof(ApiPeer))]
[JsonSerializable(typeof(IReadOnlyList<ApiPeer>))]
[JsonSerializable(typeof(InterfaceWriteResult))]
[JsonSerializable(typeof(PeerWriteResult))]
[JsonSerializable(typeof(CreatePeerResult))]
[JsonSerializable(typeof(VersionResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(Problem))]
public sealed partial class ApiJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

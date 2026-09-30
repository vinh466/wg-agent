using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WgAgent.Api;
using WgAgent.Core;
using WgAgent.Core.Model;
using WgAgent.Platform;
using WgAgent.Core.Store;
using WgAgent.Platform.Linux;
using WgAgent.Service;
using WgAgent.Testing;
using YamlDotNet.Serialization;

namespace WgAgent.Tests.Api;

/// <summary>
/// api/openapi.yaml is the source of truth (REQ-API-001, ADR-0014); these compare what the server
/// exposes with it, so a divergence fails the build rather than a client.
/// </summary>
public sealed class ContractTests
{
    private static readonly Dictionary<string, object> Doc = Load();

    private static Dictionary<string, object> Load()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "api/openapi.yaml")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(dir!, "api/openapi.yaml")));
    }

    private static Dictionary<object, object> Map(object o) => (Dictionary<object, object>)o;

    [Fact]
    public void Contract_ExposesEveryRouteAndNoOther_REQ_API_063()
    {
        var contract = new HashSet<string>();
        foreach (var (path, item) in Map(Doc["paths"]))
            foreach (var (method, op) in Map(item))
                if (method is "get" or "post" or "put" or "delete")
                    contract.Add($"{((string)method).ToUpperInvariant()} /v1{path}");

        var exposed = new HashSet<string>();
        using var app = BuildApp();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>();
        foreach (var endpoint in endpoints)
        {
            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? [];
            foreach (var method in methods)
                exposed.Add($"{method} /{endpoint.RoutePattern.RawText!.TrimStart('/')}");
        }

        Assert.Equal(contract.OrderBy(x => x), exposed.OrderBy(x => x));
    }

    [Fact]
    public void Contract_ReasonCodesAreAllDefined_REQ_API_041()
    {
        var enumValues = ((List<object>)Map(Map(Doc["components"])["schemas"]).Pipe("ReasonCode")["enum"])
            .Select(v => (string)v).ToHashSet();
        var defined = typeof(ReasonCodes).GetFields()
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet();
        Assert.Empty(enumValues.Except(defined));
    }

    [Theory]
    [MemberData(nameof(Schemas))]
    public void Contract_SchemaPropertiesMatchTheServer_REQ_API_001(string schema, string sampleJson)
    {
        var declared = Map(Map(Map(Doc["components"])["schemas"]).Pipe(schema)["properties"]).Keys
            .Select(k => (string)k).OrderBy(x => x).ToList();
        var served = JsonDocument.Parse(sampleJson).RootElement.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();
        Assert.Equal(declared, served);
    }

    public static IEnumerable<object[]> Schemas()
    {
        var iface = new InterfaceResource
        {
            Name = "wg0",
            Spec = new InterfaceSpec { ListenPort = 51820, Addresses = ["10.8.0.1/24"], Mtu = 1420, Enabled = true, Labels = new Dictionary<string, string>() },
            Status = FullInterfaceStatus,
        };
        var peer = new PeerResource
        {
            InterfaceName = "wg0", PublicKey = TestKeys.Base64(2),
            Spec = new PeerSpec { AllowedIps = ["10.8.0.2/32"], Endpoint = "198.51.100.7:51820", PersistentKeepalive = 25, Labels = new Dictionary<string, string>() },
            Status = FullPeerStatus,
        };
        yield return Case("InterfaceResource", ApiInterface.From(iface), ApiJsonContext.Default.ApiInterface);
        yield return Case("InterfaceSpecView", ApiInterface.From(iface).Spec, ApiJsonContext.Default.ApiInterfaceSpec);
        yield return Case("InterfaceStatus", FullInterfaceStatus, ApiJsonContext.Default.InterfaceStatus);
        yield return Case("PeerResource", ApiPeer.From(peer), ApiJsonContext.Default.ApiPeer);
        yield return Case("PeerSpecView", ApiPeer.From(peer).Spec, ApiJsonContext.Default.ApiPeerSpec);
        yield return Case("PeerStatus", FullPeerStatus, ApiJsonContext.Default.PeerStatus);
        yield return Case("Warning", new Warning("MTU_OUT_OF_RANGE", "m"), ApiJsonContext.Default.Warning);
        yield return Case("InterfaceWriteResult", new InterfaceWriteResult { Interface = ApiInterface.From(iface), Restarted = true }, ApiJsonContext.Default.InterfaceWriteResult);
        yield return Case("PeerWriteResult", new PeerWriteResult { Peer = ApiPeer.From(peer), Restarted = true }, ApiJsonContext.Default.PeerWriteResult);
        yield return Case("CreatePeerResult", new WgAgent.Api.CreatePeerResult { Peer = ApiPeer.From(peer), Restarted = false, PrivateKey = "k", PresharedKey = "p", ClientConfiguration = "c" }, ApiJsonContext.Default.CreatePeerResult);
        yield return Case("VersionResponse", new VersionResponse { Version = "1", Commit = "c", StartedAt = DateTimeOffset.UnixEpoch, UptimeSeconds = 1 }, ApiJsonContext.Default.VersionResponse);
        yield return Case("HealthResponse", new HealthResponse { Status = "ok" }, ApiJsonContext.Default.HealthResponse);
        yield return Case("Problem", new Problem { Title = "t", Status = 400, Detail = "d", Reason = "X" }, ApiJsonContext.Default.Problem);
        yield return Case("CreateInterfaceRequest", new CreateInterfaceRequest { Name = "wg0", PrivateKey = "k", ListenPort = 1, Addresses = [], Mtu = 1, Enabled = true, Labels = new Dictionary<string, string>() }, ApiJsonContext.Default.CreateInterfaceRequest);
        yield return Case("UpdateInterfaceRequest", new UpdateInterfaceRequest { Name = "wg0", PrivateKey = "k", ListenPort = 1, Addresses = [], Mtu = 1, Enabled = true, Labels = new Dictionary<string, string>() }, ApiJsonContext.Default.UpdateInterfaceRequest);
        yield return Case("CreatePeerRequest", new WgAgent.Api.CreatePeerRequest { PublicKey = "k", GenerateKeypair = true, GeneratePresharedKey = true, PresharedKey = "p", AllowedIps = [], Endpoint = "e", PersistentKeepalive = 1, Labels = new Dictionary<string, string>(), ClientAllowedIps = [], ClientPersistentKeepalive = 1, Dns = [], NodeEndpoint = "n" }, ApiJsonContext.Default.CreatePeerRequest);
        yield return Case("UpdatePeerRequest", new UpdatePeerRequest { PublicKey = "k", PresharedKey = "p", AllowedIps = [], Endpoint = "e", PersistentKeepalive = 1, Labels = new Dictionary<string, string>() }, ApiJsonContext.Default.UpdatePeerRequest);
    }

    private static readonly InterfaceStatus FullInterfaceStatus = new()
    {
        PublicKey = TestKeys.Base64(1), ListenPort = 51820, CreatedAt = DateTimeOffset.UnixEpoch,
        OperState = OperState.Up, PeerCount = 0, Warnings = [],
    };

    private static readonly PeerStatus FullPeerStatus = new()
    {
        LastHandshakeAt = DateTimeOffset.UnixEpoch, HandshakeAgeSeconds = 1, Online = true,
        RxBytes = 1, TxBytes = 1, ResolvedEndpoint = "198.51.100.7:51820", Warnings = [],
    };

    private static object[] Case<T>(string schema, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        [schema, JsonSerializer.Serialize(value, info)];

    private static Microsoft.AspNetCore.Builder.WebApplication BuildApp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wg-agent-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var host = new FakeHost();
        var service = new AgentService(new StateStore(Path.Combine(dir, "s.json")),
            new HostPorts(host, host, host, host, new ApplyDeadline()), new AgentOptions(), TimeProvider.System);
        var token = new TokenFile(Path.Combine(dir, "token"));
        token.Write(Secret.From("t"));
        return Server.Build(new ServerOptions { Service = service, Store = new StateStore(Path.Combine(dir, "s.json")), Token = token, ListenAddress = "127.0.0.1:0" });
    }
}

file static class YamlExtensions
{
    public static Dictionary<object, object> Pipe(this Dictionary<object, object> map, string key) =>
        (Dictionary<object, object>)map[key];
}

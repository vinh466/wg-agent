using System.Text.Json;
using WgAgent.Core;
using WgAgent.Core.Json;
using WgAgent.Core.Model;
using WgAgent.Core.Store;
using WgAgent.Platform;
using WgAgent.Service;
using WgAgent.Testing;

namespace WgAgent.Tests.Service;

public sealed class AgentServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wg-agent-service-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHost _host = new();
    private readonly ManualClock _clock = new(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));
    private readonly ApplyDeadline _deadline = new();

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private string StorePath => Path.Combine(_dir, "state.json");
    private StoreState Stored => new StateStore(StorePath).Load();
    private string StoreText => File.Exists(StorePath) ? File.ReadAllText(StorePath) : "";

    private AgentService Service(AgentOptions? options = null, IUnitManager? units = null) =>
        new(new StateStore(StorePath), new HostPorts(_host, units ?? _host, _host, _host, _deadline),
            options ?? new AgentOptions { NodeEndpoint = "vpn.example.net" }, _clock);

    private static InterfaceSpec Wg0 => new() { Addresses = ["10.8.0.1/24"] };
    private static readonly string PeerA = TestKeys.Base64(0x0A);
    private static readonly string PeerB = TestKeys.Base64(0x0B);

    private static CreatePeerRequest Byok(string key, params string[] allowedIps) =>
        new() { PublicKey = key, Spec = new PeerSpec { AllowedIps = allowedIps } };

    private static CreatePeerRequest Generated => new() { GenerateKeypair = true };

    private (AgentService Service, CreatePeerResult Result) GeneratePeer(CreatePeerRequest? request = null, InterfaceSpec? spec = null)
    {
        var service = Service();
        service.CreateInterface("wg0", spec ?? Wg0);
        return (service, service.CreatePeer("wg0", request ?? Generated));
    }

    private static Ini Client(CreatePeerResult result) => Ini.Parse(result.ClientConfiguration!);
    private static AgentException Refused(Action operation) => Assert.Throws<AgentException>(operation);

    private static string Json(InterfaceResource r) => JsonSerializer.Serialize(r, CoreJsonContext.Default.InterfaceResource);
    private static string Json(PeerResource r) => JsonSerializer.Serialize(r, CoreJsonContext.Default.PeerResource);
    private static string Json(IReadOnlyList<InterfaceResource> r) => JsonSerializer.Serialize(r, CoreJsonContext.Default.IReadOnlyListInterfaceResource);
    private static string Json(IReadOnlyList<PeerResource> r) => JsonSerializer.Serialize(r, CoreJsonContext.Default.IReadOnlyListPeerResource);

    /// <summary>Records the time left on the change's deadline whenever a unit starts.</summary>
    private sealed class DeadlineProbe(IUnitManager inner, ApplyDeadline deadline) : IUnitManager
    {
        public static readonly TimeSpan Cap = TimeSpan.FromHours(1);
        public List<TimeSpan> AtStart { get; } = [];
        public void Enable(string name) => inner.Enable(name);
        public void Disable(string name) => inner.Disable(name);
        public void Start(string name) { AtStart.Add(deadline.Remaining(Cap)); inner.Start(name); }
        public void Stop(string name) => inner.Stop(name);
        public bool IsActive(string name) => inner.IsActive(name);
    }

    // ---- SPEC-06: keys

    [Fact]
    public void CreateInterface_GeneratesAnOmittedKey_REQ_KEY_001()
    {
        Service().CreateInterface("wg0", Wg0);
        var key = Stored.Interfaces["wg0"].Spec.PrivateKey!.Reveal();
        Assert.Equal(TestKeys.Base64(0x41), key);   // the first key the host's wg generated
        Assert.Contains($"PrivateKey = {key}", _host.Files["wg0"]);

        Service().CreateInterface("wg1", new InterfaceSpec { PrivateKey = TestKeys.Secret(7), Addresses = ["10.9.0.1/24"], ListenPort = 51821 });
        Assert.Equal(TestKeys.Base64(7), Stored.Interfaces["wg1"].Spec.PrivateKey!.Reveal());
    }

    [Fact]
    public void CreatePeer_ByDefaultTakesTheCallersPublicKey_REQ_KEY_010()
    {
        var service = Service();
        service.CreateInterface("wg0", Wg0);
        var result = service.CreatePeer("wg0", Byok(PeerA, "10.8.0.2/32"));

        Assert.Equal(PeerA, result.Peer.PublicKey);
        Assert.Null(result.GeneratedPrivateKey);
        Assert.Null(result.ClientConfiguration);
        Assert.Equal(["10.8.0.2/32"], Stored.Interfaces["wg0"].Peers[PeerA].AllowedIps);
    }

    [Fact]
    public void GeneratedPrivateKey_IsReturnedInTheResponse_REQ_KEY_011()
    {
        var (_, result) = GeneratePeer();
        Assert.NotNull(result.GeneratedPrivateKey);
        Assert.Equal(_host.PublicKeyOf(result.GeneratedPrivateKey).ToString(), result.Peer.PublicKey);
    }

    [Fact]
    public void GeneratedPrivateKey_IsNeverStored_REQ_KEY_012()
    {
        var (_, result) = GeneratePeer();
        var secret = result.GeneratedPrivateKey!.Reveal();
        Assert.DoesNotContain(secret, StoreText);
        Assert.DoesNotContain(secret, _host.Files["wg0"]);
    }

    [Fact]
    public void NoLaterResponse_ReturnsAGeneratedPrivateKey_REQ_KEY_013()
    {
        var (service, result) = GeneratePeer();
        var secret = result.GeneratedPrivateKey!.Reveal();

        Assert.DoesNotContain(secret, Json(result.Peer));
        Assert.DoesNotContain(secret, Json(service.GetPeer("wg0", result.Peer.PublicKey)));
        Assert.DoesNotContain(secret, Json(service.ListPeers("wg0")));
        Assert.DoesNotContain(secret, Json(service.GetInterface("wg0")));
        Assert.DoesNotContain(secret, Json(service.ListInterfaces()));
    }

    [Fact]
    public void GeneratedPresharedKey_IsStoredAndReturnedOnce_REQ_KEY_021()
    {
        var service = Service();
        service.CreateInterface("wg0", Wg0);
        var result = service.CreatePeer("wg0", Byok(PeerA, "10.8.0.2/32") with { GeneratePresharedKey = true });
        var psk = result.GeneratedPresharedKey!.Reveal();

        Assert.Equal(psk, Stored.Interfaces["wg0"].Peers[PeerA].PresharedKey!.Reveal());
        Assert.Contains($"PresharedKey = {psk}", _host.Files["wg0"]);
        Assert.DoesNotContain(psk, Json(result.Peer));
        Assert.DoesNotContain(psk, Json(service.GetPeer("wg0", PeerA)));
        Assert.DoesNotContain(psk, Json(service.ListPeers("wg0")));
    }

    // ---- SPEC-06: the client configuration

    [Fact]
    public void ClientConfiguration_IsBuiltFromTheGeneratedKey_REQ_KEY_042()
    {
        var (_, result) = GeneratePeer();
        Assert.Equal(result.GeneratedPrivateKey!.Reveal(), Client(result).Interface["PrivateKey"]);
    }

    [Fact]
    public void ClientAddress_IsThePeersEntriesWithinTheSubnets_REQ_KEY_043()
    {
        var (_, result) = GeneratePeer(Generated with { Spec = new PeerSpec { AllowedIps = ["192.168.50.0/24", "10.8.0.9/32"] } });
        Assert.Equal("10.8.0.9/32", Client(result).Interface["Address"]);
    }

    [Fact]
    public void ClientAllowedIps_AreTheRequestsOrElseTheSubnets_REQ_KEY_044()
    {
        var (service, result) = GeneratePeer(spec: new InterfaceSpec { Addresses = ["10.8.0.1/24", "10.9.0.1/24"] });
        Assert.Equal("10.8.0.0/24, 10.9.0.0/24", Client(result).Peers[0]["AllowedIPs"]);

        var chosen = service.CreatePeer("wg0", Generated with { ClientAllowedIps = ["10.8.0.0/24", "192.168.1.0/24"] });
        Assert.Equal("10.8.0.0/24, 192.168.1.0/24", Client(chosen).Peers[0]["AllowedIPs"]);
    }

    [Fact]
    public void ClientConfiguration_CarriesAPresharedKeyGeneratedWithIt_REQ_KEY_045()
    {
        var (service, withPsk) = GeneratePeer(Generated with { GeneratePresharedKey = true });
        Assert.Equal(withPsk.GeneratedPresharedKey!.Reveal(), Client(withPsk).Peers[0]["PresharedKey"]);

        var without = service.CreatePeer("wg0", Generated);
        Assert.False(Client(without).Peers[0].ContainsKey("PresharedKey"));
    }

    [Fact]
    public void ClientKeepalive_DefaultsTo25AndIsOmittedAtZero_REQ_KEY_046()
    {
        var (service, byDefault) = GeneratePeer();
        Assert.Equal("25", Client(byDefault).Peers[0]["PersistentKeepalive"]);
        Assert.Equal("10", Client(service.CreatePeer("wg0", Generated with { ClientPersistentKeepalive = 10 })).Peers[0]["PersistentKeepalive"]);
        Assert.False(Client(service.CreatePeer("wg0", Generated with { ClientPersistentKeepalive = 0 })).Peers[0].ContainsKey("PersistentKeepalive"));
    }

    [Fact]
    public void GeneratedPeer_GetsTheLowestFreeAddress_REQ_KEY_047()
    {
        var service = Service();
        service.CreateInterface("wg0", Wg0);
        service.CreatePeer("wg0", Byok(PeerA, "10.8.0.2/32"));

        var result = service.CreatePeer("wg0", Generated);

        Assert.Equal(["10.8.0.3/32"], result.Peer.Spec.AllowedIps);
        Assert.Equal("10.8.0.3/32", Client(result).Interface["Address"]);
    }

    [Fact]
    public void Dns_ReachesTheClientConfigurationOnly_REQ_KEY_034()
    {
        var (_, result) = GeneratePeer(Generated with { Dns = ["10.8.0.1", "1.1.1.1"] });
        Assert.Equal("10.8.0.1, 1.1.1.1", Client(result).Interface["DNS"]);
        Assert.DoesNotContain("DNS", _host.Files["wg0"]);
    }

    [Fact]
    public void ClientConfiguration_IsAWgQuickFile_REQ_KEY_035()
    {
        var (service, result) = GeneratePeer(Generated with { GeneratePresharedKey = true, Dns = ["1.1.1.1"] });

        var client = Client(result);   // sections of "Key = Value" lines, or it throws
        Assert.Equal(["PrivateKey", "Address", "DNS"], client.InterfaceLines.Select(l => l.Key));
        var peer = Assert.Single(client.Peers);
        Assert.Equal(["PublicKey", "PresharedKey", "AllowedIPs", "Endpoint", "PersistentKeepalive"], peer.Keys);
        Assert.Equal(service.GetInterface("wg0").Status.PublicKey, peer["PublicKey"]);
    }

    [Fact]
    public void ClientEndpoint_IsTheNodeAtTheInterfacesPort_REQ_KEY_036()
    {
        var (_, result) = GeneratePeer(spec: Wg0 with { ListenPort = 51900 });
        Assert.Equal("vpn.example.net:51900", Client(result).Peers[0]["Endpoint"]);
    }

    [Fact]
    public void ClientEndpoint_HostIsTheRequestsOrElseTheConfigured_REQ_KEY_037()
    {
        var (service, configured) = GeneratePeer();
        Assert.Equal("vpn.example.net:51820", Client(configured).Peers[0]["Endpoint"]);

        var requested = service.CreatePeer("wg0", Generated with { NodeEndpoint = "203.0.113.5" });
        Assert.Equal("203.0.113.5:51820", Client(requested).Peers[0]["Endpoint"]);
    }

    [Fact]
    public void Generating_WithNoEndpointAnywhere_IsRefused_REQ_KEY_038()
    {
        var service = Service(new AgentOptions());
        service.CreateInterface("wg0", Wg0);
        var before = StoreText;
        _host.Calls.Clear();

        Assert.Equal(ReasonCodes.EndpointRequired, Refused(() => service.CreatePeer("wg0", Generated)).Code);
        Assert.Equal(before, StoreText);
        Assert.Empty(_host.Calls);

        Assert.NotNull(service.CreatePeer("wg0", Generated with { NodeEndpoint = "vpn.example.net" }).ClientConfiguration);
    }

    // ---- SPEC-04: write semantics

    [Fact]
    public void Write_IsValidatedAppliedThenStored_REQ_API_020()
    {
        var service = Service();
        service.CreateInterface("wg0", Wg0);
        service.CreatePeer("wg0", Byok(PeerA, "10.8.0.2/32"));
        var stored = StoreText;
        _host.Calls.Clear();

        // Refused by validation: nothing applied, nothing stored.
        Assert.Equal(ReasonCodes.AllowedIpsDuplicate, Refused(() => service.CreatePeer("wg0", Byok(PeerB, "10.8.0.2/32"))).Code);
        Assert.Empty(_host.Calls);
        Assert.Equal(stored, StoreText);

        // Failed to apply: nothing stored.
        _host.Failures.Add("sync wg0");
        Assert.Equal(ReasonCodes.ApplyFailed, Refused(() => service.CreatePeer("wg0", Byok(PeerB, "10.8.0.3/32"))).Code);
        Assert.Equal(stored, StoreText);

        // Applied, then stored.
        service.CreatePeer("wg0", Byok(PeerB, "10.8.0.3/32"));
        Assert.Contains(PeerB, _host.Devices["wg0"].Peers.Keys);
        Assert.True(Stored.Interfaces["wg0"].Peers.ContainsKey(PeerB));
    }

    [Fact]
    public void Applying_IsBoundedByApplyTimeout_REQ_API_022()
    {
        var probe = new DeadlineProbe(_host, _deadline);
        var service = Service(new AgentOptions { ApplyTimeout = TimeSpan.FromSeconds(7) }, probe);

        service.CreateInterface("wg0", Wg0);

        Assert.InRange(Assert.Single(probe.AtStart), TimeSpan.FromTicks(1), TimeSpan.FromSeconds(7));
        Assert.Equal(DeadlineProbe.Cap, _deadline.Remaining(DeadlineProbe.Cap));   // ended with the write
    }

    [Fact]
    public void Update_ReplacesTheWholeSpec_REQ_API_064()
    {
        var service = Service();
        service.CreateInterface("wg0", Wg0 with { Labels = new Dictionary<string, string> { ["site"] = "hn" } });
        service.CreatePeer("wg0", Byok(PeerA) with
        {
            Spec = new PeerSpec { AllowedIps = ["10.8.0.2/32"], PersistentKeepalive = 25, Endpoint = "198.51.100.7:51820" },
        });

        service.UpdateInterface("wg0", Wg0);
        service.UpdatePeer("wg0", PeerA, new PeerSpec { AllowedIps = ["10.8.0.2/32"] });

        var wg0 = Stored.Interfaces["wg0"];
        Assert.Empty(wg0.Spec.Labels!);
        Assert.Null(wg0.Peers[PeerA].Endpoint);
        Assert.Equal(0u, wg0.Peers[PeerA].PersistentKeepalive);
    }

    [Fact]
    public void Update_OmittedFieldTakesItsDefault_REQ_API_075()
    {
        var service = Service();
        service.CreateInterface("wg0", Wg0 with { Enabled = false, ListenPort = 51900, Mtu = 1380 });
        Assert.DoesNotContain("wg0", _host.Active);

        service.UpdateInterface("wg0", Wg0);

        var spec = Stored.Interfaces["wg0"].Spec;
        Assert.Equal(true, spec.Enabled);
        Assert.Equal(51820u, spec.ListenPort);
        Assert.Equal(1420u, spec.Mtu);
        Assert.Contains("wg0", _host.Active);
    }

    [Fact]
    public void Update_KeepsAnOmittedWriteOnlyKey_REQ_API_065()
    {
        var service = Service();
        service.CreateInterface("wg0", Wg0 with { PrivateKey = TestKeys.Secret(7) });
        service.CreatePeer("wg0", Byok(PeerA) with { Spec = new PeerSpec { AllowedIps = ["10.8.0.2/32"], PresharedKey = TestKeys.Secret(8) } });

        service.UpdateInterface("wg0", Wg0 with { Mtu = 1380 });
        service.UpdatePeer("wg0", PeerA, new PeerSpec { AllowedIps = ["10.8.0.2/32"], PersistentKeepalive = 25 });

        Assert.Equal(TestKeys.Base64(7), Stored.Interfaces["wg0"].Spec.PrivateKey!.Reveal());
        Assert.Equal(TestKeys.Base64(8), Stored.Interfaces["wg0"].Peers[PeerA].PresharedKey!.Reveal());
    }

    [Fact]
    public void ApiWrite_LeavesTheHooksAsTheCliSetThem_REQ_API_085()
    {
        var service = Service();
        service.CreateInterface("wg0", Wg0 with { PostUp = ["iptables -A FORWARD -i %i -j ACCEPT"], PostDown = ["iptables -D FORWARD -i %i -j ACCEPT"] });

        service.UpdateInterface("wg0", Wg0 with { Mtu = 1380 }, keepHooks: true);

        var spec = Stored.Interfaces["wg0"].Spec;
        Assert.Equal(["iptables -A FORWARD -i %i -j ACCEPT"], spec.PostUp);
        Assert.Equal(["iptables -D FORWARD -i %i -j ACCEPT"], spec.PostDown);
        Assert.Contains("PostUp = iptables -A FORWARD -i %i -j ACCEPT", _host.Files["wg0"]);
    }

    [Fact]
    public void Operations_RefuseAnInterfaceDesiredStateLacks_REQ_API_069()
    {
        var service = Service();
        Action[] operations =
        [
            () => service.GetInterface("wg9"),
            () => service.UpdateInterface("wg9", Wg0),
            () => service.DeleteInterface("wg9"),
            () => service.ListPeers("wg9"),
            () => service.GetPeer("wg9", PeerA),
            () => service.CreatePeer("wg9", Byok(PeerA, "10.8.0.2/32")),
            () => service.UpdatePeer("wg9", PeerA, new PeerSpec { AllowedIps = ["10.8.0.2/32"] }),
            () => service.DeletePeer("wg9", PeerA),
        ];
        Assert.All(operations, operation => Assert.Equal(ReasonCodes.InterfaceNotManaged, Refused(operation).Code));
    }

    [Fact]
    public void PeerOperations_RefuseAPeerDesiredStateLacks_REQ_API_070()
    {
        var service = Service();
        service.CreateInterface("wg0", Wg0);
        Action[] operations =
        [
            () => service.GetPeer("wg0", PeerA),
            () => service.UpdatePeer("wg0", PeerA, new PeerSpec { AllowedIps = ["10.8.0.2/32"] }),
            () => service.DeletePeer("wg0", PeerA),
        ];
        Assert.All(operations, operation => Assert.Equal(ReasonCodes.PeerNotFound, Refused(operation).Code));
    }

    [Fact]
    public void CreatePeer_RefusesAKeyAlreadyDescribed_REQ_API_071()
    {
        var service = Service();
        service.CreateInterface("wg0", Wg0);
        service.CreatePeer("wg0", Byok(PeerA, "10.8.0.2/32"));

        Assert.Equal(ReasonCodes.PeerExists, Refused(() => service.CreatePeer("wg0", Byok(PeerA, "10.8.0.3/32"))).Code);
        Assert.Equal(["10.8.0.2/32"], Stored.Interfaces["wg0"].Peers[PeerA].AllowedIps);
    }

    // ---- SPEC-01: the resources

    [Fact]
    public void InterfacesAndPeers_AreSeparateCollections_REQ_RES_002()
    {
        var service = Service();
        service.CreateInterface("wg0", Wg0);
        service.CreatePeer("wg0", Byok(PeerA, "10.8.0.2/32"));

        using var wg0 = JsonDocument.Parse(Json(service.GetInterface("wg0")));
        Assert.False(wg0.RootElement.TryGetProperty("peers", out _));
        Assert.False(wg0.RootElement.GetProperty("spec").TryGetProperty("peers", out _));
        Assert.Equal([PeerA], service.ListPeers("wg0").Select(p => p.PublicKey));
    }

    [Fact]
    public void RepeatingAWrite_LeavesTheStateOfDoingItOnce_REQ_RES_003()
    {
        var service = Service();
        void Twice(Action write, Action repeat)
        {
            write();
            var once = StoreText;
            try { repeat(); } catch (AgentException) { }
            Assert.Equal(once, StoreText);
        }

        Twice(() => service.CreateInterface("wg0", Wg0), () => service.CreateInterface("wg0", Wg0));
        Twice(() => service.CreatePeer("wg0", Byok(PeerA, "10.8.0.2/32")), () => service.CreatePeer("wg0", Byok(PeerA, "10.8.0.2/32")));
        var keepalive = new PeerSpec { AllowedIps = ["10.8.0.2/32"], PersistentKeepalive = 25 };
        Twice(() => service.UpdatePeer("wg0", PeerA, keepalive), () => service.UpdatePeer("wg0", PeerA, keepalive));
        Twice(() => service.UpdateInterface("wg0", Wg0 with { Mtu = 1380 }), () => service.UpdateInterface("wg0", Wg0 with { Mtu = 1380 }));
        Twice(() => service.DeletePeer("wg0", PeerA), () => service.DeletePeer("wg0", PeerA));
        Twice(() => service.DeleteInterface("wg0"), () => service.DeleteInterface("wg0"));
    }

    [Fact]
    public void InterfaceName_CannotChange_REQ_RES_010()
    {
        Assert.DoesNotContain(typeof(InterfaceSpec).GetProperties(), p => p.Name == "Name");   // no write can carry one
        var service = Service();
        service.CreateInterface("wg0", Wg0);

        Assert.Equal("wg0", service.UpdateInterface("wg0", Wg0 with { Mtu = 1380 }).Resource.Name);
        Assert.Equal(["wg0"], Stored.Interfaces.Keys);
    }

    [Fact]
    public void PeerIdentity_CannotChange_REQ_RES_020()
    {
        Assert.DoesNotContain(typeof(PeerSpec).GetProperties(), p => p.Name is "PublicKey" or "InterfaceName");
        var service = Service();
        service.CreateInterface("wg0", Wg0);
        service.CreatePeer("wg0", Byok(PeerA, "10.8.0.2/32"));

        var updated = service.UpdatePeer("wg0", PeerA, new PeerSpec { AllowedIps = ["10.8.0.9/32"] }).Resource;
        Assert.Equal(("wg0", PeerA), (updated.InterfaceName, updated.PublicKey));
        Assert.Equal([PeerA], Stored.Interfaces["wg0"].Peers.Keys);
    }

    [Fact]
    public void NoResponse_CarriesThePrivateKey_REQ_RES_013()
    {
        var service = Service();
        string[] responses =
        [
            Json(service.CreateInterface("wg0", Wg0 with { PrivateKey = TestKeys.Secret(7) }).Resource),
            Json(service.UpdateInterface("wg0", Wg0 with { Mtu = 1380 }).Resource),
            Json(service.GetInterface("wg0")),
            Json(service.ListInterfaces()),
        ];
        Assert.All(responses, response => Assert.DoesNotContain(TestKeys.Base64(7), response));
        Assert.All(responses, response => Assert.DoesNotContain("private_key", response));
    }

    [Fact]
    public void NoResponse_CarriesAStoredPresharedKey_REQ_RES_022()
    {
        var service = Service();
        service.CreateInterface("wg0", Wg0);
        var withPsk = Byok(PeerA) with { Spec = new PeerSpec { AllowedIps = ["10.8.0.2/32"], PresharedKey = TestKeys.Secret(8) } };
        string[] responses =
        [
            Json(service.CreatePeer("wg0", withPsk).Peer),
            Json(service.UpdatePeer("wg0", PeerA, new PeerSpec { AllowedIps = ["10.8.0.2/32"] }).Resource),
            Json(service.GetPeer("wg0", PeerA)),
            Json(service.ListPeers("wg0")),
        ];
        Assert.All(responses, response => Assert.DoesNotContain(TestKeys.Base64(8), response));
        Assert.All(responses, response => Assert.DoesNotContain("preshared_key", response));
    }

    // ---- SPEC-03: what the agent deletes

    [Fact]
    public void ForeignInterface_IsNeitherModifiedNorDeleted_REQ_RCN_030()
    {
        _host.Files["wg9"] = $"[Interface]\nPrivateKey = {TestKeys.Base64(9)}\nListenPort = 51999\nAddress = 10.99.0.1/24\n";
        _host.Start("wg9");
        var file = _host.Files["wg9"];
        _host.Calls.Clear();
        var service = Service();

        Refused(() => service.DeleteInterface("wg9"));
        Refused(() => service.UpdateInterface("wg9", Wg0));
        Refused(() => service.CreatePeer("wg9", Byok(PeerA, "10.99.0.2/32")));

        Assert.Empty(_host.Calls);
        Assert.Contains("wg9", _host.Active);
        Assert.Equal(file, _host.Files["wg9"]);
    }

    [Fact]
    public void DeleteInterface_StopsDisablesAndForgetsIt_REQ_RCN_032()
    {
        var service = Service();
        service.CreateInterface("wg0", Wg0);
        _host.Calls.Clear();

        service.DeleteInterface("wg0");

        Assert.Equal(["stop wg0", "disable wg0", "delete wg0"], _host.Calls);
        Assert.DoesNotContain("wg0", _host.Files.Keys);
        Assert.DoesNotContain("wg0", _host.Enabled);
        Assert.Empty(Stored.Interfaces);
    }

    [Fact]
    public void DeleteInterface_TakesItsPeersInTheSameWrite_REQ_RCN_038()
    {
        var service = Service();
        service.CreateInterface("wg0", Wg0);
        service.CreatePeer("wg0", Byok(PeerA, "10.8.0.2/32"));
        service.CreatePeer("wg0", Byok(PeerB, "10.8.0.3/32"));

        service.DeleteInterface("wg0");

        Assert.DoesNotContain(PeerA, StoreText);
        Assert.DoesNotContain(PeerB, StoreText);
        service.CreateInterface("wg0", Wg0);
        Assert.Empty(service.ListPeers("wg0"));
    }

    // ---- SPEC-13: the response and failure

    [Fact]
    public void WriteResponse_StatesARestart_REQ_APL_007()
    {
        var service = Service();
        service.CreateInterface("wg0", Wg0);

        Assert.False(service.CreatePeer("wg0", Byok(PeerA, "10.8.0.2/32")).Restarted);
        Assert.True(service.UpdateInterface("wg0", Wg0 with { Mtu = 1380 }).Restarted);
        Assert.True(service.CreatePeer("wg0", Byok(PeerB, "192.168.60.0/24")).Restarted);   // a site-to-site peer
    }

    [Fact]
    public void StoreFailure_UndoesTheAppliedChange_REQ_APL_011()
    {
        Directory.CreateDirectory(StorePath);   // a directory where the store file belongs: every save fails
        var service = Service();

        Assert.Equal(ReasonCodes.ApplyFailed, Refused(() => service.CreateInterface("wg0", Wg0)).Code);

        Assert.Contains("start wg0", _host.Calls);          // applied...
        Assert.DoesNotContain("wg0", _host.Files.Keys);     // ...and undone
        Assert.DoesNotContain("wg0", _host.Active);
        Assert.DoesNotContain("wg0", _host.Enabled);
    }

    [Fact]
    public void Restoration_IsBoundedOnItsOwn_REQ_APL_012()
    {
        var probe = new DeadlineProbe(_host, _deadline);
        var service = Service(new AgentOptions { ApplyTimeout = TimeSpan.FromSeconds(7) }, probe);
        service.CreateInterface("wg0", Wg0);
        probe.AtStart.Clear();

        _host.Failures.Add("start wg0");   // the restart an address change needs fails
        Assert.Equal(ReasonCodes.ApplyFailed, Refused(() => service.UpdateInterface("wg0", new InterfaceSpec { Addresses = ["10.8.1.1/24"] })).Code);

        Assert.Equal(2, probe.AtStart.Count);
        Assert.InRange(probe.AtStart[0], TimeSpan.Zero, TimeSpan.FromSeconds(7));   // the change, within its deadline
        Assert.Equal(DeadlineProbe.Cap, probe.AtStart[1]);                           // the restoration, on its own bound
        Assert.Contains("wg0", _host.Active);
    }

    // ---- SPEC-07: findings

    [Fact]
    public void ErrorFinding_BlocksTheWrite_REQ_VAL_001()
    {
        var service = Service();
        Assert.Equal(ReasonCodes.AddressesRequired, Refused(() => service.CreateInterface("wg0", new InterfaceSpec { Addresses = [] })).Code);
        Assert.Empty(_host.Calls);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void WarningFinding_IsReportedWithoutBlocking_REQ_VAL_002()
    {
        var service = Service();
        var created = service.CreateInterface("wg0", Wg0 with { Mtu = 1200 });
        Assert.Contains(created.Resource.Status.Warnings, w => w.Code == ReasonCodes.MtuOutOfRange);
        Assert.Contains(service.GetInterface("wg0").Status.Warnings, w => w.Code == ReasonCodes.MtuOutOfRange);

        var peer = service.CreatePeer("wg0", Byok(PeerB, "192.168.60.0/24")).Peer;
        Assert.Contains(peer.Status.Warnings, w => w.Code == ReasonCodes.AllowedIpsOutOfSubnet);
    }

    // ---- SPEC-12: the JSON document of interface create

    [Fact]
    public void Document_CreatesTheInterfaceAndItsPeersInOneWrite_REQ_CLI_025()
    {
        var service = Service();
        var created = service.CreateInterface("wg0", Wg0 with { PrivateKey = TestKeys.Secret(7) },
        [
            (PeerA, new PeerSpec { AllowedIps = ["10.8.0.2/32"] }),
            (PeerB, new PeerSpec { AllowedIps = ["10.8.0.3/32"], PresharedKey = TestKeys.Secret(8) }),
        ]);

        Assert.Equal(["write wg0", "enable wg0", "start wg0"], _host.Calls.Where(c => !c.StartsWith("show")));
        Assert.Equal(2, created.Resource.Status.PeerCount);
        Assert.Equal([PeerA, PeerB], Stored.Interfaces["wg0"].Peers.Keys.Order(StringComparer.Ordinal));

        var bad = Service();
        Assert.Equal(ReasonCodes.AllowedIpsDuplicate, Refused(() => bad.CreateInterface("wg1", new InterfaceSpec { Addresses = ["10.9.0.1/24"], ListenPort = 51821 },
        [
            (PeerA, new PeerSpec { AllowedIps = ["10.9.0.2/32"] }),
            (PeerB, new PeerSpec { AllowedIps = ["10.9.0.2/32"] }),
        ])).Code);
        Assert.DoesNotContain("wg1", _host.Files.Keys);   // one mistake leaves nothing behind
        Assert.False(Stored.Interfaces.ContainsKey("wg1"));
    }

    // ---- SPEC-05: secrets

    [Fact]
    public void EveryResponse_IsFreeOfSecrets_REQ_SEC_051()
    {
        var service = Service();
        var responses = new List<string>
        {
            Json(service.CreateInterface("wg0", Wg0 with { PrivateKey = TestKeys.Secret(7) }).Resource),
            Json(service.CreatePeer("wg0", Byok(PeerA) with { Spec = new PeerSpec { AllowedIps = ["10.8.0.2/32"], PresharedKey = TestKeys.Secret(8) } }).Peer),
        };
        var generated = service.CreatePeer("wg0", Generated with { GeneratePresharedKey = true });
        responses.Add(Json(generated.Peer));
        responses.Add(Json(service.UpdateInterface("wg0", Wg0 with { Mtu = 1380 }).Resource));
        responses.Add(Json(service.UpdatePeer("wg0", PeerA, new PeerSpec { AllowedIps = ["10.8.0.2/32"] }).Resource));
        responses.Add(Json(service.GetInterface("wg0")));
        responses.Add(Json(service.ListInterfaces()));
        responses.Add(Json(service.GetPeer("wg0", PeerA)));
        responses.Add(Json(service.ListPeers("wg0")));
        responses.Add(Refused(() => service.CreatePeer("wg0", Byok(PeerB) with
            { Spec = new PeerSpec { AllowedIps = ["10.8.0.2/32"], PresharedKey = TestKeys.Secret(9) } })).Message);
        responses.Add(Refused(() => service.CreateInterface("wg1", new InterfaceSpec { PrivateKey = TestKeys.Secret(0x1A), Addresses = ["10.8.0.5/24"], ListenPort = 51821 })).Message);

        string[] secrets =
        [
            TestKeys.Base64(7), TestKeys.Base64(8), TestKeys.Base64(9), TestKeys.Base64(0x1A),
            generated.GeneratedPrivateKey!.Reveal(), generated.GeneratedPresharedKey!.Reveal(),
        ];
        foreach (var response in responses)
            Assert.All(secrets, secret => Assert.DoesNotContain(secret, response));
    }
}

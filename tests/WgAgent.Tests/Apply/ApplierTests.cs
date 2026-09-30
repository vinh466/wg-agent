using WgAgent.Core;
using WgAgent.Core.Apply;
using WgAgent.Core.Model;
using WgAgent.Core.Render;
using WgAgent.Core.Store;
using WgAgent.Testing;

namespace WgAgent.Tests.Apply;

public class ApplierTests
{
    private readonly FakeHost _host = new();
    private Applier Applier => new(_host, _host, _host);

    private static readonly string A = TestKeys.Base64(0xA1), B = TestKeys.Base64(0xB2);

    private static StoredInterface Iface(bool enabled = true, string address = "10.8.0.1/24", uint mtu = 1420,
        string[]? postUp = null, string[]? postDown = null, params (string Key, string[] Ips)[] peers) => new(
        Defaults.Apply(new InterfaceSpec
        {
            PrivateKey = TestKeys.Secret(1), Addresses = [address], Mtu = mtu, Enabled = enabled,
            PostUp = postUp, PostDown = postDown,
        }),
        DateTimeOffset.UnixEpoch,
        peers.ToDictionary(p => p.Key, p => Defaults.Apply(new PeerSpec { AllowedIps = p.Ips })));

    private void Given(StoredInterface state) { Applier.Apply("wg0", null, state); _host.Calls.Clear(); _host.Hooks.Clear(); }

    [Fact]
    public void AnInterface_IsAFileAndAUnit_REQ_APL_001()
    {
        Applier.Apply("wg0", null, Iface());
        Assert.True(_host.Files.ContainsKey("wg0"));
        Assert.Contains("wg0", _host.Enabled);
        Assert.Contains("wg0", _host.Active);
        Assert.True(_host.Devices.ContainsKey("wg0"));
    }

    [Fact]
    public void OtherFiles_AreNeverTouched_REQ_APL_002()
    {
        _host.Files["wg9"] = "# kept by hand";
        Applier.Apply("wg0", null, Iface());
        Applier.Apply("wg0", Iface(), Iface(address: "10.8.1.1/24"));
        Applier.Apply("wg0", Iface(address: "10.8.1.1/24"), null);
        Assert.Equal("# kept by hand", _host.Files["wg9"]);
        Assert.DoesNotContain("wg9", _host.Reads);
        Assert.DoesNotContain(_host.Calls, c => c.EndsWith(" wg9"));
    }

    [Fact]
    public void ARenderedFile_CarriesOnlyTheClosedListOfKeys_REQ_APL_003()
    {
        var spec = Defaults.Apply(new InterfaceSpec
        {
            PrivateKey = TestKeys.Secret(1), Addresses = ["10.8.0.1/24"], PostUp = ["iptables -A x"], PostDown = ["iptables -D x"],
            Labels = new Dictionary<string, string> { ["DNS"] = "1.1.1.1", ["Table"] = "off" },
        });
        var peers = new Dictionary<string, PeerSpec>
        {
            [A] = Defaults.Apply(new PeerSpec { AllowedIps = ["10.8.0.2/32"], PresharedKey = TestKeys.Secret(7), Endpoint = "203.0.113.1:51820", PersistentKeepalive = 25 }),
        };
        var ini = Ini.Parse(ConfigRenderer.File(spec, peers));
        Assert.Subset(new HashSet<string> { "PrivateKey", "ListenPort", "Address", "MTU", "PostUp", "PostDown" }, ini.InterfaceLines.Select(l => l.Key).ToHashSet());
        foreach (var peer in ini.Peers)
            Assert.Subset(new HashSet<string> { "PublicKey", "PresharedKey", "AllowedIPs", "Endpoint", "PersistentKeepalive" }, peer.Keys.ToHashSet());
        Assert.Equal("51820", ini.Interface["ListenPort"]);
    }

    [Fact]
    public void TheUnit_IsEnabledAndActiveExactlyWhenEnabled_REQ_APL_004()
    {
        Applier.Apply("wg0", null, Iface(enabled: false));
        Assert.True(_host.Files.ContainsKey("wg0"));
        Assert.DoesNotContain("wg0", _host.Enabled);
        Assert.DoesNotContain("wg0", _host.Active);

        Applier.Apply("wg0", Iface(enabled: false), Iface(enabled: true));
        Assert.Contains("wg0", _host.Enabled);
        Assert.Contains("wg0", _host.Active);

        Applier.Apply("wg0", Iface(enabled: true), Iface(enabled: false));
        Assert.DoesNotContain("wg0", _host.Enabled);
        Assert.DoesNotContain("wg0", _host.Active);
        Assert.True(_host.Files.ContainsKey("wg0"));
    }

    [Fact]
    public void APeerChange_LeavesTheOtherPeersSessionsAlone_REQ_APL_005()
    {
        var one = Iface(peers: (A, ["10.8.0.2/32"]));
        Given(one);
        var session = _host.Devices["wg0"].Peers[A].Session;

        var two = Iface(peers: [(A, ["10.8.0.2/32"]), (B, ["10.8.0.3/32"])]);
        var result = Applier.Apply("wg0", one, two);
        Applier.Apply("wg0", two, one);

        Assert.False(result.Restarted);
        Assert.Contains("sync wg0", _host.Calls);
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("stop") || c.StartsWith("start"));
        Assert.Equal(session, _host.Devices["wg0"].Peers[A].Session);
        Assert.Single(_host.Devices["wg0"].Peers);
    }

    [Fact]
    public void AddressesMtuHooksAndSiteToSitePeers_Restart_REQ_APL_006()
    {
        var baseline = Iface(peers: (A, ["10.8.0.2/32"]));
        var siteToSite = Iface(peers: [(A, ["10.8.0.2/32"]), (B, ["10.8.0.3/32", "192.168.50.0/24"])]);
        (StoredInterface From, StoredInterface To)[] restarting =
        [
            (baseline, Iface(address: "10.8.0.1/16", peers: (A, ["10.8.0.2/32"]))),
            (baseline, Iface(mtu: 1380, peers: (A, ["10.8.0.2/32"]))),
            (baseline, Iface(postUp: ["iptables -A x"], peers: (A, ["10.8.0.2/32"]))),
            (baseline, siteToSite),                                                            // added
            (siteToSite, Iface(peers: [(A, ["10.8.0.2/32"]), (B, ["10.8.0.3/32", "192.168.60.0/24"])])), // changed
            (siteToSite, baseline),                                                            // removed
        ];
        foreach (var (from, to) in restarting)
        {
            var host = new FakeHost();
            var applier = new Applier(host, host, host);
            applier.Apply("wg0", null, from);
            host.Calls.Clear();
            Assert.True(applier.Apply("wg0", from, to).Restarted);
            Assert.Equal(["stop wg0", "write wg0", "start wg0"], host.Calls);
        }

        // On a disabled interface a change rewrites the file alone.
        Given(Iface(enabled: false));
        Applier.Apply("wg0", Iface(enabled: false), Iface(enabled: false, address: "10.8.0.1/16"));
        Assert.Equal(["write wg0"], _host.Calls);
    }

    [Fact]
    public void TheResult_SaysWhetherSessionsWereInterrupted_REQ_APL_007()
    {
        Given(Iface());
        Assert.False(Applier.Apply("wg0", Iface(), Iface(peers: (A, ["10.8.0.2/32"]))).Restarted);
        Assert.True(Applier.Apply("wg0", Iface(peers: (A, ["10.8.0.2/32"])), Iface(mtu: 1300, peers: (A, ["10.8.0.2/32"]))).Restarted);
    }

    [Fact]
    public void AFailure_RestoresThePreviousFileAndState_REQ_APL_008()
    {
        // A restart whose start fails: the old file comes back up.
        var before = Iface(peers: (A, ["10.8.0.2/32"]));
        Given(before);
        var oldText = _host.Files["wg0"];
        _host.Failures.Add("start wg0");
        var error = Assert.Throws<AgentException>(() => Applier.Apply("wg0", before, Iface(mtu: 1300, peers: (A, ["10.8.0.2/32"]))));
        Assert.Equal("APPLY_FAILED", error.Code);
        Assert.Equal(oldText, _host.Files["wg0"]);
        Assert.Contains("wg0", _host.Active);

        // A synchronisation that fails: undone by synchronising back, with no restart.
        _host.Calls.Clear();
        var session = _host.Devices["wg0"].Peers[A].Session;
        _host.Failures.Add("sync wg0");
        Assert.Equal("APPLY_FAILED", Assert.Throws<AgentException>(() =>
            Applier.Apply("wg0", before, Iface(peers: [(A, ["10.8.0.2/32"]), (B, ["10.8.0.3/32"])]))).Code);
        Assert.Equal(oldText, _host.Files["wg0"]);
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("stop"));
        Assert.Equal(session, _host.Devices["wg0"].Peers[A].Session);

        // A create whose start fails: nothing is left behind.
        var fresh = new FakeHost();
        fresh.Failures.Add("start wg1");
        Assert.Throws<AgentException>(() => new Applier(fresh, fresh, fresh).Apply("wg1", null, Iface()));
        Assert.False(fresh.Files.ContainsKey("wg1"));
        Assert.DoesNotContain("wg1", fresh.Enabled);
    }

    [Fact]
    public void TheUnit_StopsBeforeItsFileChanges_REQ_APL_010()
    {
        var before = Iface(postUp: ["add-old"], postDown: ["del-old"]);
        Given(before);
        Applier.Apply("wg0", before, Iface(postUp: ["add-new"], postDown: ["del-new"]));
        Assert.Equal(["down: del-old", "up: add-new"], _host.Hooks);
        Assert.Equal(["stop wg0", "write wg0", "start wg0"], _host.Calls);

        _host.Calls.Clear();
        Applier.Apply("wg0", Iface(postUp: ["add-new"], postDown: ["del-new"]), Iface(enabled: false, postUp: ["add-new"], postDown: ["del-new"]));
        Assert.True(_host.Calls.IndexOf("stop wg0") < _host.Calls.IndexOf("write wg0"));

        Given(Iface());
        Applier.Apply("wg0", Iface(), null);
        Assert.True(_host.Calls.IndexOf("stop wg0") < _host.Calls.IndexOf("delete wg0"));
    }
}

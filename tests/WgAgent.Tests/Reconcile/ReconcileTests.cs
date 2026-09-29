using WgAgent.Core.Model;
using WgAgent.Core.Reconcile;
using WgAgent.Platform;
using WgAgent.Testing;
using StoreHandle = WgAgent.Core.Store.Store;

namespace WgAgent.Tests.Reconcile;

// SPEC-03. The algorithm of REQ-RCN-022, driven against the in-memory platform:
// desired state lives in a real store, kernel state in a FakeNode, and each test
// injects drift and asserts convergence. Method names embed the REQ ID.
public sealed class ReconcileTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ReconcileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wg-agent-rcn-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "state.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static string Pub() => Key.Generate().GetPublicKey().ToBase64();

    private static InterfaceSpec Iface(
        string address = "10.100.0.1/24", int port = 51820,
        bool enabled = true, bool manageRoutes = false, int mtu = 0) => new()
    {
        PrivateKey = Key.Generate().ToBase64(),
        ListenPort = port,
        Addresses = [address],
        Enabled = enabled,
        ManageRoutes = manageRoutes,
        Mtu = mtu,
        ForwardPolicy = ForwardPolicySpec.Default(),
    };

    private static Peer PeerWith(string pub, string allowed = "10.100.0.2/32") => new()
    {
        InterfaceName = "wg0",
        PublicKey = pub,
        Spec = new PeerSpec { AllowedIPs = [allowed] },
    };

    private StoreHandle OpenSeeded(Action<WgAgent.Core.Store.Txn> seed)
    {
        var store = StoreHandle.Open(_path);
        store.Update(seed);
        return store;
    }

    private static Engine EngineFor(StoreHandle store, FakeNode node)
        => new(store, node.Link(), node, node, () => DateTimeOffset.UnixEpoch);

    [Fact]
    public void Converges_On_An_Interface_With_No_Link_REQ_RCN_022()
    {
        using var store = OpenSeeded(t => t.PutInterface("wg0", Iface(), "i", "t"));
        var node = new FakeNode();
        var engine = EngineFor(store, node);

        var st = engine.Interface("wg0");

        Assert.Equal(ConditionState.Ready, st.Condition.State);
        Assert.True(node.Did("LinkAdd(wg0)"));
        Assert.True(node.Did("Configure(wg0)"));
        Assert.True(node.Did("AddrAdd(wg0,10.100.0.1/24)"));
        Assert.True(node.Did("LinkSetUp(wg0)"));
        Assert.Equal(OperState.Up, st.OperState);
    }

    [Fact]
    public void A_Second_Pass_Writes_Nothing_REQ_RES_003()
    {
        using var store = OpenSeeded(t =>
        {
            t.PutInterface("wg0", Iface(), "i", "t");
            t.PutPeer("wg0", PeerWith(Pub()));
        });
        var node = new FakeNode();
        var engine = EngineFor(store, node);

        engine.Interface("wg0");
        node.ResetCalls();
        var st = engine.Interface("wg0");

        Assert.Equal(ConditionState.Ready, st.Condition.State);
        Assert.Empty(node.Calls); // an interface already in its desired state is untouched
    }

    [Fact]
    public void Removes_A_Peer_The_Kernel_Holds_But_Desired_State_Does_Not_REQ_RCN_011()
    {
        using var store = OpenSeeded(t =>
        {
            t.PutInterface("wg0", Iface(), "i", "t");
            t.PutPeer("wg0", PeerWith(Pub()));
        });
        var node = new FakeNode();
        var engine = EngineFor(store, node);
        engine.Interface("wg0");

        // A peer appears in the kernel that desired state does not name.
        node.AddPeer("wg0", Pub(), "10.100.0.99/32");
        node.ResetCalls();

        engine.Interface("wg0");
        Assert.True(node.Did("RemovePeer(wg0,"));
    }

    [Fact]
    public void Does_Not_Overwrite_An_Endpoint_The_Kernel_Learned_REQ_RCN_051()
    {
        string pub = Pub();
        using var store = OpenSeeded(t =>
        {
            t.PutInterface("wg0", Iface(), "i", "t");
            t.PutPeer("wg0", PeerWith(pub)); // no endpoint in desired state
        });
        var node = new FakeNode();
        var engine = EngineFor(store, node);
        engine.Interface("wg0");

        // The kernel learns an endpoint from a handshake.
        var d = node.Devices["wg0"];
        node.Devices["wg0"] = d with { Peers = [d.Peers[0] with { Endpoint = "203.0.113.9:51820" }] };
        node.ResetCalls();

        engine.Interface("wg0");
        Assert.False(node.Did("SetEndpoint"));
        Assert.Equal("203.0.113.9:51820", node.Devices["wg0"].Peers[0].Endpoint);
    }

    [Fact]
    public void A_Wrong_Type_Link_Is_Degraded_REQ_RCN_022()
    {
        using var store = OpenSeeded(t => t.PutInterface("wg0", Iface(), "i", "t"));
        var node = new FakeNode().AddInterface("wg0", "10.100.0.1/24", 51820).WithLinkType("wg0", "veth");
        var engine = EngineFor(store, node);

        var st = engine.Interface("wg0");
        Assert.Equal(ConditionState.Degraded, st.Condition.State);
        Assert.Equal(ReconcileReasons.ReconcileFailed, st.Condition.Reason);
    }

    [Fact]
    public void A_Failure_On_One_Interface_Does_Not_Stop_Another_REQ_RCN_040()
    {
        using var store = OpenSeeded(t =>
        {
            t.PutInterface("wg0", Iface(), "i", "t");
            t.PutInterface("wg1", Iface("10.101.0.1/24", 51821), "i", "t");
        });
        var node = new FakeNode().AddInterface("wg1", "10.101.0.1/24", 51821).WithLinkType("wg1", "veth");
        var engine = EngineFor(store, node);

        var r = engine.Pass();

        Assert.Equal(1, r.Failed);
        Assert.Equal(2, r.Total);
        Assert.True(engine.TryGetStatus("wg0", out var st0));
        Assert.Equal(ConditionState.Ready, st0.Condition.State);
        Assert.True(engine.TryGetStatus("wg1", out var st1));
        Assert.Equal(ConditionState.Degraded, st1.Condition.State);
    }

    [Fact]
    public void An_Undescribed_Link_Is_Foreign_And_Untouched_REQ_RCN_036()
    {
        using var store = OpenSeeded(_ => { });
        var node = new FakeNode().AddInterface("wgX", "10.5.0.1/24", 51000);
        var engine = EngineFor(store, node);

        engine.Pass();

        Assert.True(engine.TryGetStatus("wgX", out var st));
        Assert.Equal(Ownership.Foreign, st.Ownership);
        Assert.Empty(node.Calls); // REQ-RCN-030 leaves a foreign link alone
    }

    [Fact]
    public void A_Deletion_Record_With_A_Live_Link_Is_Orphaned_REQ_RCN_034()
    {
        using var store = OpenSeeded(t => t.PutDeletion("wg9", "t"));
        var node = new FakeNode().AddInterface("wg9", "10.9.0.1/24", 51000);
        var engine = EngineFor(store, node);

        engine.Pass();

        Assert.True(engine.TryGetStatus("wg9", out var st));
        Assert.Equal(Ownership.Orphaned, st.Ownership);
        Assert.Empty(node.Calls); // REQ-RCN-035 forbids acting on an orphan
    }

    [Fact]
    public void A_Deletion_Record_Whose_Link_Is_Gone_Is_Cleared_REQ_RCN_037()
    {
        using var store = OpenSeeded(t => t.PutDeletion("wg8", "t"));
        var node = new FakeNode(); // no wg8 link on the host
        var engine = EngineFor(store, node);

        engine.Pass();

        Assert.False(store.DeletionRecord("wg8"));
    }

    [Fact]
    public void Writes_The_Forwarding_Sysctl_When_Policy_Needs_It_REQ_FWD_020()
    {
        using var store = OpenSeeded(t => t.PutInterface("wg0", Iface(), "i", "t"));
        var node = new FakeNode();
        var engine = EngineFor(store, node);

        engine.Interface("wg0");
        Assert.True(node.Did("SetForwarding(wg0,1)"));
    }

    [Fact]
    public void A_Read_Only_Sysctl_Degrades_The_Interface_REQ_FWD_020()
    {
        using var store = OpenSeeded(t => t.PutInterface("wg0", Iface(), "i", "t"));
        var node = new FakeNode().WithReadOnlySysctl();
        var engine = EngineFor(store, node);

        var st = engine.Interface("wg0");
        Assert.Equal(ConditionState.Degraded, st.Condition.State);
        Assert.Equal(ReconcileReasons.SysctlWriteDenied, st.Condition.Reason);
    }

    [Fact]
    public void Syncs_Routes_From_The_Union_Of_Allowed_IPs_REQ_RCN_022()
    {
        using var store = OpenSeeded(t =>
        {
            t.PutInterface("wg0", Iface(manageRoutes: true), "i", "t");
            t.PutPeer("wg0", PeerWith(Pub(), "10.100.0.2/32"));
        });
        var node = new FakeNode();
        var engine = EngineFor(store, node);

        engine.Interface("wg0");
        Assert.True(node.Did("RouteAdd(wg0,10.100.0.2/32)"));
    }

    [Fact]
    public void Skips_Routes_While_The_Link_Is_Down_REQ_RCN_024()
    {
        using var store = OpenSeeded(t =>
        {
            t.PutInterface("wg0", Iface(manageRoutes: true, enabled: false), "i", "t");
            t.PutPeer("wg0", PeerWith(Pub()));
        });
        var node = new FakeNode();
        var engine = EngineFor(store, node);

        engine.Interface("wg0");
        Assert.False(node.Did("RouteAdd"));
    }

    [Fact]
    public void Sets_The_Mtu_From_The_Spec_REQ_RCN_022()
    {
        using var store = OpenSeeded(t => t.PutInterface("wg0", Iface(mtu: 1400), "i", "t"));
        var node = new FakeNode();
        var engine = EngineFor(store, node);

        engine.Interface("wg0");
        Assert.True(node.Did("SetMtu(wg0,1400)"));
    }
}

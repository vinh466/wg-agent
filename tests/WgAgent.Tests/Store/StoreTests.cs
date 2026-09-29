using WgAgent.Core.Model;
using WgAgent.Core.Store;
using WgAgent.Platform;

namespace WgAgent.Tests.Store;

// SPEC-03. The store is a single JSON file written atomically under an advisory
// lock. Each method embeds the REQ ID it verifies.
public sealed class StoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public StoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wg-agent-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "state.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static string Pub() => Key.Generate().GetPublicKey().ToBase64();

    private static InterfaceSpec Iface() => new()
    {
        ListenPort = 51820,
        Addresses = ["10.100.0.1/24"],
        ForwardPolicy = ForwardPolicySpec.Default(),
    };

    [Fact]
    public void RoundTrip_Interface_And_Peer_REQ_RCN_002()
    {
        string pub = Pub();
        using (var store = WgAgent.Core.Store.Store.Open(_path))
        {
            store.Update(t =>
            {
                t.PutInterface("wg0", Iface(), "inst-1", "2026-01-01T00:00:00Z");
                t.PutPeer("wg0", new Peer
                {
                    InterfaceName = "wg0",
                    PublicKey = pub,
                    Spec = new PeerSpec { AllowedIPs = ["10.100.0.2/32"] },
                });
            });
        }

        // Reopen from disk to prove the write reached the file, not just memory.
        var snap = Snapshot.Read(_path);
        Assert.Equal(["wg0"], snap.Names());
        Assert.True(snap.TryGetInterface("wg0", out var spec));
        Assert.Equal(51820, spec.ListenPort);
        Assert.Equal(Axis.Deny, spec.ForwardPolicy.External); // enum survived the round trip
        var peers = snap.Peers("wg0");
        Assert.Single(peers);
        Assert.Equal(pub, peers[0].PublicKey);
    }

    [Fact]
    public void Update_Rolls_Back_On_Throw_REQ_RCN_064()
    {
        using var store = WgAgent.Core.Store.Store.Open(_path);
        store.Update(t => t.PutInterface("wg0", Iface(), "inst-1", "t"));

        Assert.Throws<InvalidOperationException>(() => store.Update(t =>
        {
            t.PutInterface("wg1", Iface(), "inst-2", "t");
            throw new InvalidOperationException("adoption failed part-way");
        }));

        // The failed transaction changed nothing, on disk or in memory.
        Assert.Equal(["wg0"], store.Names());
        Assert.Equal(["wg0"], Snapshot.Read(_path).Names());
    }

    [Fact]
    public void Second_Open_Is_Refused_While_Locked_REQ_RCN_007()
    {
        using var first = WgAgent.Core.Store.Store.Open(_path);
        Assert.Throws<StoreLockedException>(() => WgAgent.Core.Store.Store.Open(_path));
    }

    [Fact]
    public void Lock_Is_Released_On_Dispose_REQ_RCN_006()
    {
        using (var first = WgAgent.Core.Store.Store.Open(_path)) { }
        // The lock is gone, so a fresh handle opens without error.
        using var second = WgAgent.Core.Store.Store.Open(_path);
    }

    [Fact]
    public void File_Is_Mode_0600_REQ_RCN_004()
    {
        using (var store = WgAgent.Core.Store.Store.Open(_path))
        {
            store.Update(t => t.PutInterface("wg0", Iface(), "inst-1", "t"));
        }
        var mode = File.GetUnixFileMode(_path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Schema_Newer_Than_Build_Is_Refused_REQ_RCN_005()
    {
        File.WriteAllText(_path, "{\"schema\":99}");
        Assert.Throws<SchemaTooNewException>(() => Snapshot.Read(_path));
    }

    [Fact]
    public void Absent_Store_Is_Empty_REQ_CLI_004()
    {
        var snap = Snapshot.Read(Path.Combine(_dir, "never-written.db"));
        Assert.Empty(snap.Names());
        Assert.False(snap.Describes("wg0"));
    }

    [Fact]
    public void Adoption_Record_Carries_Forwarding_Baseline_REQ_RCN_070()
    {
        using var store = WgAgent.Core.Store.Store.Open(_path);
        store.Update(t =>
        {
            t.PutInterface("wg0", Iface(), "inst-1", "t");
            t.PutAdoption("wg0", "1", "t");
        });
        Assert.True(store.Current().AdoptionRecord("wg0"));
        store.Update(t =>
        {
            Assert.True(t.TryGetForwardingBaseline("wg0", out string baseline)); // REQ-FWD-024
            Assert.Equal("1", baseline);
        });
    }

    [Fact]
    public void Deletion_Record_Recorded_And_Cleared_REQ_RCN_037()
    {
        using var store = WgAgent.Core.Store.Store.Open(_path);
        store.Update(t => t.PutDeletion("wg0", "t")); // REQ-RCN-033
        Assert.True(store.DeletionRecord("wg0"));
        store.ClearDeletion("wg0");
        Assert.False(store.DeletionRecord("wg0"));
    }

    [Fact]
    public void RemoveInterface_Clears_Spec_Peers_And_Adoption_REQ_RCN_071()
    {
        using var store = WgAgent.Core.Store.Store.Open(_path);
        store.Update(t =>
        {
            t.PutInterface("wg0", Iface(), "inst-1", "t");
            t.PutPeer("wg0", new Peer { PublicKey = Pub(), Spec = new PeerSpec { AllowedIPs = ["10.100.0.2/32"] } });
            t.PutAdoption("wg0", "0", "t");
        });

        store.Update(t => t.RemoveInterface("wg0"));

        Assert.False(store.Describes("wg0"));
        Assert.Empty(store.Peers("wg0"));
        Assert.False(store.Current().AdoptionRecord("wg0"));
    }
}

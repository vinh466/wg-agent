using WgAgent.Core.Apply;
using WgAgent.Core.Model;
using WgAgent.Core.Status;
using WgAgent.Core.Store;
using WgAgent.Testing;

namespace WgAgent.Tests.Status;

public class StatusReaderTests
{
    private static readonly string Peer = TestKeys.Base64(0xA1);
    private readonly FakeHost _host = new();
    private readonly ManualClock _clock = new(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));
    private StatusReader Reader => new(_host, _host, _clock, TimeSpan.FromSeconds(180));

    private readonly StoredInterface _wg0 = new(
        Defaults.Apply(new InterfaceSpec { PrivateKey = TestKeys.Secret(1), Addresses = ["10.8.0.1/24"] }),
        DateTimeOffset.UnixEpoch,
        new Dictionary<string, PeerSpec> { [Peer] = Defaults.Apply(new PeerSpec { AllowedIps = ["10.8.0.2/32"] }) });

    private void Up() => new Applier(_host, _host, _host).Apply("wg0", null, _wg0);

    private InterfaceStatus Iface() => StatusReader.Interface(Reader.Observe("wg0"), _wg0, "pk", []);

    [Fact]
    public void OperState_FollowsTheUnitAndTheDevice_REQ_RES_035()
    {
        Up();
        Assert.Equal(OperState.Up, Iface().OperState);
        _host.Devices.Remove("wg0");                 // removed by hand, unit still active
        Assert.Equal(OperState.Absent, Iface().OperState);
        _host.Active.Remove("wg0");
        Assert.Equal(OperState.Down, Iface().OperState);
    }

    [Fact]
    public void WithoutADevice_KernelFieldsAreNull_REQ_RES_036()
    {
        var status = Iface();                        // never brought up
        Assert.Null(status.ListenPort);
        Assert.Null(status.PeerCount);
        Assert.Equal("pk", status.PublicKey);
        var peer = Reader.Peer(Reader.Observe("wg0"), Peer, []);
        Assert.Null(peer.RxBytes);
        Assert.Null(peer.TxBytes);
        Assert.Null(peer.LastHandshakeAt);
        Assert.Null(peer.ResolvedEndpoint);
        Assert.False(peer.Online);
    }

    [Fact]
    public void ListenPort_IsThePortTheKernelHolds_REQ_RES_016()
    {
        Up();
        _host.Devices["wg0"].ListenPort = 51999;     // changed with wg set behind the agent's back
        Assert.Equal(51999u, Iface().ListenPort);
    }

    [Fact]
    public void NoHandshakeYet_IsNullNotTheEpoch_REQ_RES_023()
    {
        Up();
        var peer = Reader.Peer(Reader.Observe("wg0"), Peer, []);
        Assert.Null(peer.LastHandshakeAt);
        Assert.Null(peer.HandshakeAgeSeconds);

        _host.Devices["wg0"].Peers[Peer].LatestHandshake = _clock.Now.AddSeconds(-42);
        var later = Reader.Peer(Reader.Observe("wg0"), Peer, []);
        Assert.Equal(42, later.HandshakeAgeSeconds);
        Assert.True(later.Online);
    }

    [Fact]
    public void KernelFields_AreReadOnEveryCall_REQ_RES_024()
    {
        Up();
        _host.Devices["wg0"].Peers[Peer].RxBytes = 100;
        Assert.Equal(100, Reader.Peer(Reader.Observe("wg0"), Peer, []).RxBytes);
        _host.Devices["wg0"].Peers[Peer].RxBytes = 250;
        Assert.Equal(250, Reader.Peer(Reader.Observe("wg0"), Peer, []).RxBytes);
        Assert.Equal(2, _host.Calls.Count(c => c == "show wg0"));
    }
}

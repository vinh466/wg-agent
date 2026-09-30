namespace WgAgent.IntegrationTests;

/// <summary>
/// The exit criteria of P1 on a real node. Each test owns its interface wgN on 10.N.0.1/24, port
/// 51820+N, and its client cN, so no test depends on another or on the order they run in.
/// </summary>
public sealed class NodeTests(Node node) : IClassFixture<Node>
{
    /// <summary>A client in a network namespace of its own, joined to the node by a veth pair; returns the node's end.</summary>
    private string Client(int n)
    {
        string server = $"192.0.2.{4 * n + 1}", client = $"192.0.2.{4 * n + 2}";
        node.Must($"""
            ip netns add c{n}
            ip link add s{n} type veth peer name v{n}
            ip link set v{n} netns c{n}
            ip addr add {server}/30 dev s{n} && ip link set s{n} up
            ip -n c{n} addr add {client}/30 dev v{n} && ip -n c{n} link set v{n} up && ip -n c{n} link set lo up
            """);
        return server;
    }

    /// <summary>Creates wgN and connects a client to it with the file a generated peer printed.</summary>
    private void Connected(int n)
    {
        var server = Client(n);
        node.Must($"wg-agent interface create wg{n} --addresses 10.{n}.0.1/24 --listen-port {51820 + n}");
        // The client's file lives in /etc/wireguard/: the host's AppArmor confines wg-quick to it.
        node.Must($"umask 077; wg-agent peer add wg{n} --generate-keypair --node-endpoint {server} > /etc/wireguard/c{n}.conf");
        node.Must($"ip netns exec c{n} wg-quick up c{n}");
        node.Must($"ip netns exec c{n} ping -c 3 -W 2 10.{n}.0.1");
    }

    private string NewPublicKey() => node.Must("wg genkey | wg pubkey").Trim();

    [Fact]
    public void GeneratedPeer_ConnectsWithThePrintedFile_REQ_KEY_042()
    {
        Connected(1);   // the ping through the tunnel is the proof

        var handshake = node.Must("wg show wg1 latest-handshakes").Trim().Split('\t')[1];
        Assert.NotEqual("0", handshake);
    }

    [Fact]
    public void InterfaceCreated_IsItsFileAndItsUnit_REQ_APL_001()
    {
        node.Must("wg-agent interface create wg7 --addresses 10.7.0.1/24 --listen-port 51827");

        Assert.Equal("0600", node.Must("stat -c %a /etc/wireguard/wg7.conf").Trim().PadLeft(4, '0'));
        Assert.Equal("active", node.Run("systemctl is-active wg-quick@wg7").Out.Trim());
        Assert.Equal("51827", node.Must("wg show wg7 listen-port").Trim());
    }

    [Fact]
    public void PeerAdded_WhileAnotherPings_LosesNothing_REQ_APL_005()
    {
        Connected(2);

        var added = node.Run($"""
            ip netns exec c2 ping -i 0.1 -c 50 -W 1 10.2.0.1 > /tmp/ping2.log & pinging=$!
            sleep 1
            wg-agent peer add wg2 --public-key {NewPublicKey()} --allowed-ips 10.2.0.9/32 > /dev/null || exit 9
            wait $pinging
            grep -o '[0-9.]*% packet loss' /tmp/ping2.log
            """);

        Assert.Equal(0, added.Code);
        Assert.Equal("0% packet loss", added.Out.Trim());
        Assert.DoesNotContain("restarted", added.Error);
        Assert.Equal(2, node.Must("wg show wg2 peers").Trim().Split('\n').Length);
    }

    [Fact]
    public void SiteToSitePeer_RestartsTheUnitWithItsRoute_REQ_APL_006()
    {
        node.Must("wg-agent interface create wg3 --addresses 10.3.0.1/24 --listen-port 51823");

        var added = node.Run($"wg-agent peer add wg3 --public-key {NewPublicKey()} --allowed-ips 192.168.53.0/24 --output json");

        Assert.Equal(0, added.Code);
        Assert.Contains("\"restarted\": true", added.Out);
        Assert.Contains("dev wg3", node.Must("ip route show 192.168.53.0/24"));
        Assert.Equal("active", node.Run("systemctl is-active wg-quick@wg3").Out.Trim());
    }

    [Fact]
    public void DeletedInterface_LeavesNothingBehind_REQ_RCN_032()
    {
        node.Must("wg-agent interface create wg4 --addresses 10.4.0.1/24 --listen-port 51824");
        node.Must($"wg-agent peer add wg4 --public-key {NewPublicKey()} --allowed-ips 10.4.0.2/32");

        node.Must("wg-agent interface delete wg4");

        Assert.NotEqual(0, node.Run("test -e /etc/wireguard/wg4.conf").Code);
        Assert.NotEqual(0, node.Run("ip link show wg4").Code);
        Assert.NotEqual("enabled", node.Run("systemctl is-enabled wg-quick@wg4").Out.Trim());
        Assert.DoesNotContain("wg4", node.Must("wg-agent interface list"));
    }

    [Fact]
    public void EnabledInterface_ComesBackAtBootWithoutTheAgent_REQ_APL_004()
    {
        node.Must("wg-agent interface create wg5 --addresses 10.5.0.1/24 --listen-port 51825");
        var peer = NewPublicKey();
        node.Must($"wg-agent peer add wg5 --public-key {peer} --allowed-ips 10.5.0.2/32");
        node.Must("wg-agent interface create wg6 --addresses 10.6.0.1/24 --listen-port 51826 --enabled false");

        node.Reboot();   // nothing of the agent runs at boot: systemd brings up what is enabled

        Assert.Contains(peer, node.Must("wg show wg5 peers"));
        Assert.Contains("10.5.0.1/24", node.Must("ip -4 addr show wg5"));
        Assert.NotEqual(0, node.Run("ip link show wg6").Code);
        Assert.NotEqual("enabled", node.Run("systemctl is-enabled wg-quick@wg6").Out.Trim());
    }

    [Fact]
    public void ConcurrentWriters_AreSerialised_REQ_RCN_042()
    {
        node.Must("wg-agent interface create wg8 --addresses 10.8.0.1/24 --listen-port 51828");

        var writers = node.Run("""
            for i in 2 3 4 5 6 7; do
              (wg-agent peer add wg8 --public-key "$(wg genkey | wg pubkey)" --allowed-ips 10.8.0.$i/32 --apply-timeout 60s > /dev/null) &
            done
            failed=0; for job in $(jobs -p); do wait $job || failed=1; done; exit $failed
            """, TimeSpan.FromMinutes(2));

        Assert.Equal(0, writers.Code);
        Assert.Equal(6, node.Must("wg show wg8 peers").Trim().Split('\n').Length);
        Assert.Equal("6", node.Must("grep -c '^\\[Peer\\]' /etc/wireguard/wg8.conf").Trim());
        Assert.Equal("6", node.Must("wg-agent peer list wg8 --output json | grep -c '\"public_key\"'").Trim());
    }
}

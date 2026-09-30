using System.Text.Json;

namespace WgAgent.IntegrationTests;

/// <summary>
/// The P2 exit criteria on a real node: every operation of SPEC-04 answers over HTTP with the token
/// and refuses without it, and the CLI and the API change the one node side by side. serve runs as a
/// transient systemd unit, so it survives the exec that starts it.
/// </summary>
public sealed class NodeApiTests : IClassFixture<Node>, IDisposable
{
    private const string Base = "http://127.0.0.1:9585/v1";
    private static readonly object Gate = new();
    private static string? _token;   // serve is started once per container; the token is shared across the class's tests
    private readonly Node _node;
    private readonly string _token1;

    public NodeApiTests(Node node)
    {
        _node = node;
        lock (Gate)
        {
            if (_token is null)
            {
                _token = node.Must("wg-agent token rotate --token-file /etc/wg-agent/token").Trim();
                node.Must("""
                    systemd-run --unit=wg-agent --setenv=WG_AGENT_NODE_ENDPOINT=203.0.113.1 \
                      wg-agent serve --listen-address 127.0.0.1:9585 \
                      --token-file /etc/wg-agent/token --state-path /var/lib/wg-agent/state.json
                    """);
                node.Must("""for i in $(seq 1 50); do [ "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:9585/v1/health)" = 200 ] && exit 0; sleep 0.2; done; echo "serve did not come up"; journalctl -u wg-agent --no-pager | tail; exit 1""",
                    TimeSpan.FromMinutes(1));
            }
            _token1 = _token;
        }
    }

    public void Dispose() { }

    private sealed record Call(int Code, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body.Length == 0 ? "null" : Body).RootElement;
    }

    /// <summary>Runs curl inside the node; the method, path, an optional body, and whether to send the token.</summary>
    private Call Api(string method, string path, string? body = null, bool auth = true)
    {
        var header = auth ? $"-H 'Authorization: Bearer {_token1}'" : "";
        var data = body is null ? "" : $"-H 'Content-Type: application/json' -d '{body}'";
        var output = _node.Must($"curl -s -o /tmp/body -w '%{{http_code}}' -X {method} {header} {data} {Base}{path}; echo; cat /tmp/body");
        var lines = output.Split('\n', 2);
        return new Call(int.Parse(lines[0].Trim()), lines.Length > 1 ? lines[1] : "");
    }

    [Fact]
    public void EveryOperation_AnswersWithTheToken_REQ_API_063()
    {
        Assert.Equal(201, Api("POST", "/interfaces", """{"name":"api0","addresses":["10.20.0.1/24"],"listen_port":52000}""").Code);
        Assert.Equal(200, Api("GET", "/interfaces").Code);
        Assert.Equal(200, Api("GET", "/interfaces/api0").Code);
        Assert.Equal(200, Api("PUT", "/interfaces/api0", """{"addresses":["10.20.0.1/24"],"listen_port":52000,"mtu":1380}""").Code);

        var peerKey = _node.Must("wg genkey | wg pubkey").Trim();
        var segment = peerKey.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(201, Api("POST", "/interfaces/api0/peers", $$"""{"public_key":"{{peerKey}}","allowed_ips":["10.20.0.2/32"]}""").Code);
        Assert.Equal(200, Api("GET", "/interfaces/api0/peers").Code);
        Assert.Equal(200, Api("GET", $"/interfaces/api0/peers/{segment}").Code);
        Assert.Equal(204, Api("DELETE", $"/interfaces/api0/peers/{segment}").Code);
        Assert.Equal(204, Api("DELETE", "/interfaces/api0").Code);
    }

    [Fact]
    public void WithoutTheToken_ItRefuses_REQ_SEC_071()
    {
        var refused = Api("GET", "/interfaces", auth: false);
        Assert.Equal(401, refused.Code);
        Assert.Equal("TOKEN_INVALID", refused.Json.GetProperty("reason").GetString());

        Assert.Equal(200, Api("GET", "/health", auth: false).Code);   // health needs none (REQ-SEC-080)
    }

    [Fact]
    public void GeneratedPeer_OverHttp_ConnectsWithItsFile_REQ_KEY_042()
    {
        Assert.Equal(201, Api("POST", "/interfaces", """{"name":"api1","addresses":["10.21.0.1/24"],"listen_port":52001}""").Code);

        var created = Api("POST", "/interfaces/api1/peers", """{"generate_keypair":true,"node_endpoint":"127.0.0.1"}""");
        Assert.Equal(201, created.Code);
        var config = created.Json.GetProperty("client_configuration").GetString()!;
        Assert.Contains("127.0.0.1:52001", config);

        // Bring the client up in its own namespace and handshake through the tunnel.
        _node.Must("ip netns add apicli; ip link add sa type veth peer name va; ip link set va netns apicli");
        _node.Must("ip addr add 198.51.100.1/30 dev sa && ip link set sa up");
        _node.Must("ip -n apicli addr add 198.51.100.2/30 dev va && ip -n apicli link set va up && ip -n apicli link set lo up");
        // The endpoint the file carries is 127.0.0.1, unreachable from the client namespace; point it at the veth.
        _node.Must($"printf '%s' '{config}' | sed 's/127.0.0.1:52001/198.51.100.1:52001/' > /etc/wireguard/apicli.conf");
        _node.Must("ip netns exec apicli wg-quick up apicli");
        _node.Must("ip netns exec apicli ping -c 3 -W 2 10.21.0.1");
    }

    [Fact]
    public void TheCliAndTheApi_ChangeOneNodeSideBySide_REQ_CLI_023()
    {
        // The CLI creates the interface; the API sees it.
        _node.Must("wg-agent interface create api2 --addresses 10.22.0.1/24 --listen-port 52002");
        Assert.Equal(200, Api("GET", "/interfaces/api2").Code);

        // The API adds a peer; the CLI sees it.
        var peerKey = _node.Must("wg genkey | wg pubkey").Trim();
        Assert.Equal(201, Api("POST", "/interfaces/api2/peers", $$"""{"public_key":"{{peerKey}}","allowed_ips":["10.22.0.2/32"]}""").Code);
        Assert.Contains(peerKey, _node.Must("wg-agent peer list api2"));

        _node.Must("wg-agent interface delete api2");
    }

    [Fact]
    public void AnUntrustworthyTokenFile_RefusesToServe_REQ_SEC_082()
    {
        _node.Must("id probe >/dev/null 2>&1 || useradd -M probe; cp /etc/wg-agent/token /tmp/badtoken; chown probe /tmp/badtoken; chmod 600 /tmp/badtoken");

        // The token check (3) precedes the capability check (4), so serve refuses on the untrustworthy
        // file before it ever binds; running it directly lets docker exec capture the reason.
        var refused = _node.Run("wg-agent serve --token-file /tmp/badtoken --state-path /tmp/badstate.json --listen-address 127.0.0.1:52099", TimeSpan.FromSeconds(30));

        Assert.NotEqual(0, refused.Code);
        Assert.Contains("TOKEN_INVALID", refused.Out + refused.Error);
    }

    [Fact]
    public void Shutdown_DrainsGracefullyAndLeavesInterfacesUp_REQ_API_073_REQ_API_074()
    {
        // A second listener of its own, so stopping it does not disturb the shared one.
        var token = _node.Must("wg-agent token rotate --token-file /tmp/sd-token").Trim();
        _node.Must("systemd-run --unit=wg-agent-sd wg-agent serve --listen-address 127.0.0.1:9586 --token-file /tmp/sd-token --state-path /tmp/sd-state.json");
        _node.Must("""for i in $(seq 1 50); do [ "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:9586/v1/health)" = 200 ] && exit 0; sleep 0.2; done; exit 1""", TimeSpan.FromMinutes(1));

        var created = _node.Must($"curl -s -o /dev/null -w '%{{http_code}}' -X POST -H 'Authorization: Bearer {token}' -H 'Content-Type: application/json' -d '{{\"name\":\"sd0\",\"addresses\":[\"10.30.0.1/24\"],\"listen_port\":52030}}' http://127.0.0.1:9586/v1/interfaces");
        Assert.Equal("201", created.Trim());

        _node.Must("systemctl stop wg-agent-sd");   // SIGTERM: ASP.NET drains and exits cleanly (REQ-API-073)

        Assert.Equal("success", _node.Must("systemctl show wg-agent-sd -p Result --value").Trim());   // graceful, not killed
        Assert.Equal(0, _node.Run("ip link show sd0").Code);   // the interface kept running (REQ-API-074)

        _node.Run("wg-quick down sd0 2>/dev/null; rm -f /etc/wireguard/sd0.conf; systemctl reset-failed wg-agent-sd 2>/dev/null");
    }
}

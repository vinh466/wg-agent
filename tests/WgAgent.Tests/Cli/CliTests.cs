using System.Text.Json;
using WgAgent.Cli;
using WgAgent.Cli.Configuration;
using WgAgent.Core.Store;
using WgAgent.Platform;
using WgAgent.Service;
using WgAgent.Testing;
using CliRunner = WgAgent.Cli.Cli;

namespace WgAgent.Tests.Cli;

public sealed class CliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wg-agent-cli-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHost _host = new();
    private readonly Dictionary<string, string> _environment = [];

    public CliTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string StorePath => Path.Combine(_dir, "state.json");
    private StoreState Stored => new StateStore(StorePath).Load();

    private sealed class TestHost(FakeHost host) : ICliHost
    {
        public HostPorts Ports(AgentConfiguration configuration) => new(host, host, host, host, new ApplyDeadline());
    }

    private sealed record Result(int Code, string Out, string Error);

    /// <summary>Runs the CLI against the in-memory host, a store of its own and no configuration file.</summary>
    private Result Run(params string[] args) =>
        RunAs([.. args, "--state-path", StorePath, "--config", Path.Combine(_dir, "absent"), "--node-endpoint", "vpn.example.net"]);

    private Result RunAs(string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var clock = new ManualClock(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));
        var code = CliRunner.Run(args, new CliContext(new TestHost(_host), output, error, _environment, clock));
        return new Result(code, output.ToString(), error.ToString());
    }

    private Result Ok(params string[] args)
    {
        var result = Run(args);
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Error}");
        return result;
    }

    private static readonly string PeerA = TestKeys.Base64(0x0A);
    private static readonly string PeerB = TestKeys.Base64(0x0B);

    private string Document(string json)
    {
        var path = Path.Combine(_dir, "wg0.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static readonly string MigratedDocument = $$"""
        {
          "spec": {
            "private_key": "{{TestKeys.Base64(0x17)}}",
            "listen_port": 51821,
            "addresses": ["10.8.0.1/24"],
            "post_up": ["iptables -A FORWARD -i %i -j ACCEPT"],
            "post_down": ["iptables -D FORWARD -i %i -j ACCEPT"]
          },
          "peers": [
            { "public_key": "{{PeerA}}", "spec": { "allowed_ips": ["10.8.0.2/32"], "preshared_key": "{{TestKeys.Base64(0x18)}}" } },
            { "public_key": "{{PeerB}}", "spec": { "allowed_ips": ["10.8.0.3/32"] } }
          ]
        }
        """;

    // ---- Section 2: the subcommands

    [Fact]
    public void Subcommands_AreThoseOfTheTable_REQ_CLI_001()
    {
        Assert.Contains("interface", Ok("--help").Out);
        foreach (var verb in new[] { "create", "list", "get", "update", "delete" }) Assert.Contains(verb, Ok("interface", "--help").Out);
        foreach (var verb in new[] { "add", "list", "get", "update", "remove" }) Assert.Contains(verb, Ok("peer", "--help").Out);
        Assert.Equal(0, Run("version").Code);
    }

    [Fact]
    public void Subcommand_ActsOnTheStoreAndInterfacesDirectly_REQ_CLI_002()
    {
        Ok("interface", "create", "wg0", "--addresses", "10.8.0.1/24");

        Assert.True(Stored.Interfaces.ContainsKey("wg0"));
        Assert.Contains("wg0", _host.Files.Keys);
        Assert.Contains("wg0", _host.Active);
    }

    [Fact]
    public void Subcommand_HasTheOperationsEffectAndValidation_REQ_CLI_023()
    {
        var refused = Run("interface", "create", "wg0", "--addresses", "fd00::1/64");
        Assert.Equal(1, refused.Code);
        Assert.StartsWith("wg-agent: IPV6_NOT_SUPPORTED:", refused.Error);
        Assert.Empty(_host.Calls);

        Ok("interface", "create", "wg0", "--addresses", "10.8.0.1/24");
        Assert.Contains("PEER_EXISTS", Run("peer", "add", "wg0", "--public-key", PeerA, "--allowed-ips", "10.8.0.2/32") is { Code: 0 }
            ? Run("peer", "add", "wg0", "--public-key", PeerA, "--allowed-ips", "10.8.0.3/32").Error : "");
    }

    [Fact]
    public void Hooks_ReachTheInterfaceThroughTheCli_REQ_CLI_024()
    {
        Ok("interface", "create", "wg0", "--addresses", "10.8.0.1/24", "--post-up", "iptables -A FORWARD -i %i -j ACCEPT", "--post-down", "iptables -D FORWARD -i %i -j ACCEPT");
        Assert.Contains("PostUp = iptables -A FORWARD -i %i -j ACCEPT", _host.Files["wg0"]);
        Assert.Contains("up: iptables -A FORWARD -i wg0 -j ACCEPT", _host.Hooks);

        Ok("interface", "update", "wg0", "--post-up", "true");
        Assert.Equal(["true"], Stored.Interfaces["wg0"].Spec.PostUp);
        Assert.Equal(["iptables -D FORWARD -i %i -j ACCEPT"], Stored.Interfaces["wg0"].Spec.PostDown);
    }

    [Fact]
    public void Document_CreatesTheInterfaceAndItsPeersInOneWrite_REQ_CLI_025()
    {
        Ok("interface", "create", "wg0", "--file", Document(MigratedDocument));

        Assert.Equal(["write wg0", "enable wg0", "start wg0"], _host.Calls.Where(c => !c.StartsWith("show")));
        var wg0 = Stored.Interfaces["wg0"];
        Assert.Equal(TestKeys.Base64(0x17), wg0.Spec.PrivateKey!.Reveal());
        Assert.Equal([PeerA, PeerB], wg0.Peers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(TestKeys.Base64(0x18), wg0.Peers[PeerA].PresharedKey!.Reveal());
        Assert.Contains("PostUp = iptables -A FORWARD -i %i -j ACCEPT", _host.Files["wg0"]);
    }

    [Fact]
    public void Document_IsTheSpecAndThePeers_REQ_CLI_034()
    {
        Ok("interface", "create", "wg0", "--file", Document($$"""{ "spec": { "addresses": ["10.8.0.1/24"] } }"""));   // peers may be absent
        Assert.Empty(Stored.Interfaces["wg0"].Peers);

        var named = Run("interface", "create", "wg1", "--file", Document($$"""{ "name": "wg1", "spec": { "addresses": ["10.9.0.1/24"], "listen_port": 51821 } }"""));
        Assert.StartsWith("wg-agent: FIELD_UNKNOWN:", named.Error);   // the name is the argument, not the document's
    }

    [Fact]
    public void Document_WithAMemberItsSchemaLacks_IsRefused_REQ_VAL_050()
    {
        var typo = Run("interface", "create", "wg0", "--file", Document($$"""
            { "spec": { "addresses": ["10.8.0.1/24"], "listen_prot": 51821 },
              "peers": [ { "public_key": "{{PeerA}}", "spec": { "allowed_ips": ["10.8.0.2/32"], "keepalive": 25 } } ] }
            """));

        Assert.Equal(1, typo.Code);
        Assert.Contains("FIELD_UNKNOWN", typo.Error);
        Assert.Contains("spec.listen_prot", typo.Error);
        Assert.Empty(_host.Calls);
        Assert.False(File.Exists(StorePath));

        var nested = Run("interface", "create", "wg0", "--file", Document($$"""
            { "spec": { "addresses": ["10.8.0.1/24"] }, "peers": [ { "public_key": "{{PeerA}}", "spec": { "allowed_ips": ["10.8.0.2/32"], "keepalive": 25 } } ] }
            """));
        Assert.Contains("peers[0].spec.keepalive", nested.Error);
    }

    [Fact]
    public void Document_WithAFieldFlag_IsRefused_REQ_CLI_035()
    {
        var both = Run("interface", "create", "wg0", "--file", Document(MigratedDocument), "--mtu", "1380");
        Assert.Equal(1, both.Code);
        Assert.Empty(_host.Calls);
    }

    // ---- Section 2.1: flags

    [Fact]
    public void Flags_AreNamedAfterTheirFieldsOncePerEntry_REQ_CLI_026()
    {
        Ok("interface", "create", "wg0", "--addresses", "10.8.0.1/24", "--addresses", "10.9.0.1/24", "--listen-port", "51900", "--labels", "site=hn", "--labels", "env=prod");
        Ok("peer", "add", "wg0", "--public-key", PeerA, "--allowed-ips", "10.8.0.2/32", "--allowed-ips", "192.168.1.0/24", "--persistent-keepalive", "25");

        var wg0 = Stored.Interfaces["wg0"];
        Assert.Equal(["10.8.0.1/24", "10.9.0.1/24"], wg0.Spec.Addresses);
        Assert.Equal(51900u, wg0.Spec.ListenPort);
        Assert.Equal(new Dictionary<string, string> { ["site"] = "hn", ["env"] = "prod" }, wg0.Spec.Labels);
        Assert.Equal(["10.8.0.2/32", "192.168.1.0/24"], wg0.Peers[PeerA].AllowedIps);
        Assert.Equal(25u, wg0.Peers[PeerA].PersistentKeepalive);
    }

    [Fact]
    public void Update_ChangesOnlyTheFieldsItsFlagsName_REQ_CLI_027()
    {
        Ok("interface", "create", "wg0", "--addresses", "10.8.0.1/24", "--labels", "site=hn", "--post-up", "true");
        Ok("peer", "add", "wg0", "--public-key", PeerA, "--allowed-ips", "10.8.0.2/32", "--endpoint", "198.51.100.7:51820");

        Ok("interface", "update", "wg0", "--mtu", "1380");
        Ok("peer", "update", "wg0", PeerA, "--persistent-keepalive", "25");

        var wg0 = Stored.Interfaces["wg0"];
        Assert.Equal(1380u, wg0.Spec.Mtu);
        Assert.Equal(["10.8.0.1/24"], wg0.Spec.Addresses);
        Assert.Equal("hn", wg0.Spec.Labels!["site"]);
        Assert.Equal(["true"], wg0.Spec.PostUp);
        Assert.Equal("198.51.100.7:51820", wg0.Peers[PeerA].Endpoint);
        Assert.Equal(25u, wg0.Peers[PeerA].PersistentKeepalive);

        Ok("interface", "update", "wg0", "--labels", "env=prod");   // a map replaced whole
        Assert.Equal(new Dictionary<string, string> { ["env"] = "prod" }, Stored.Interfaces["wg0"].Spec.Labels);
    }

    [Fact]
    public void NoArgument_CarriesAKey_REQ_CLI_028()
    {
        Ok("interface", "create", "wg0", "--addresses", "10.8.0.1/24");
        Assert.NotEqual(0, Run("interface", "update", "wg0", "--private-key", TestKeys.Base64(0x17)).Code);
        Assert.NotEqual(0, Run("peer", "add", "wg0", "--public-key", PeerA, "--allowed-ips", "10.8.0.2/32", "--preshared-key", TestKeys.Base64(0x18)).Code);
        foreach (var help in new[] { Ok("interface", "create", "--help"), Ok("interface", "update", "--help"), Ok("peer", "add", "--help"), Ok("peer", "update", "--help") })
        {
            Assert.DoesNotContain("--private-key", help.Out);
            Assert.DoesNotContain("--preshared-key", help.Out);
        }
    }

    [Fact]
    public void EmptyValue_ClearsItsField_REQ_CLI_029()
    {
        Ok("interface", "create", "wg0", "--addresses", "10.8.0.1/24", "--post-up", "true", "--mtu", "1380");
        Ok("peer", "add", "wg0", "--public-key", PeerA, "--allowed-ips", "10.8.0.2/32", "--endpoint", "198.51.100.7:51820", "--persistent-keepalive", "25");

        Ok("interface", "update", "wg0", "--post-up", "", "--mtu", "");
        Ok("peer", "update", "wg0", PeerA, "--endpoint", "", "--persistent-keepalive", "");

        var wg0 = Stored.Interfaces["wg0"];
        Assert.Empty(wg0.Spec.PostUp!);
        Assert.Equal(1420u, wg0.Spec.Mtu);               // no value: the default
        Assert.Null(wg0.Peers[PeerA].Endpoint);
        Assert.Equal(0u, wg0.Peers[PeerA].PersistentKeepalive);
    }

    // ---- Section 5: output

    [Fact]
    public void EveryOutput_HasAJsonForm_REQ_CLI_020()
    {
        string[][] commands =
        [
            ["interface", "create", "wg0", "--addresses", "10.8.0.1/24"],
            ["interface", "update", "wg0", "--mtu", "1380"],
            ["interface", "get", "wg0"],
            ["interface", "list"],
            ["peer", "add", "wg0", "--public-key", PeerA, "--allowed-ips", "10.8.0.2/32"],
            ["peer", "update", "wg0", PeerA, "--persistent-keepalive", "25"],
            ["peer", "get", "wg0", PeerA],
            ["peer", "list", "wg0"],
            ["version"],
        ];
        foreach (var command in commands)
            JsonDocument.Parse(Ok([.. command, "--output", "json"]).Out).Dispose();
    }

    [Fact]
    public void Failure_ExitsNonZeroWithItsReasonOnStandardError_REQ_CLI_021()
    {
        var failed = Run("interface", "get", "wg9");
        Assert.Equal(1, failed.Code);
        Assert.StartsWith("wg-agent: INTERFACE_NOT_MANAGED:", failed.Error);
        Assert.Empty(failed.Out);
    }

    [Fact]
    public void TextOutput_CarriesNoKeyItDidNotGenerate_REQ_CLI_022()
    {
        string[] stored = [TestKeys.Base64(0x17), TestKeys.Base64(0x18)];
        var outputs = new List<Result>
        {
            Ok("interface", "create", "wg0", "--file", Document(MigratedDocument)),
            Ok("interface", "get", "wg0"),
            Ok("interface", "list"),
            Ok("interface", "update", "wg0", "--mtu", "1380"),
            Ok("peer", "get", "wg0", PeerA),
            Ok("peer", "list", "wg0"),
            Ok("peer", "update", "wg0", PeerA, "--persistent-keepalive", "25"),
            Run("peer", "add", "wg0", "--public-key", PeerB, "--allowed-ips", "10.8.0.9/32"),
        };
        var generated = Ok("peer", "add", "wg0", "--generate-keypair", "--generate-preshared-key");
        outputs.Add(generated);

        foreach (var output in outputs)
            Assert.All(stored, secret => Assert.DoesNotContain(secret, output.Out + output.Error));
        var client = Ini.Parse(generated.Out);
        Assert.Equal(44, client.Interface["PrivateKey"].Length);   // the one key it generated
    }

    [Fact]
    public void JsonOutput_OfAReadIsItsResource_REQ_CLI_030()
    {
        Ok("interface", "create", "wg0", "--addresses", "10.8.0.1/24");
        Ok("peer", "add", "wg0", "--public-key", PeerA, "--allowed-ips", "10.8.0.2/32");

        using var wg0 = JsonDocument.Parse(Ok("interface", "get", "wg0", "--output", "json").Out);
        Assert.Equal(["name", "spec", "status"], wg0.RootElement.EnumerateObject().Select(p => p.Name));
        using var list = JsonDocument.Parse(Ok("peer", "list", "wg0", "--output", "json").Out);
        Assert.Equal(PeerA, Assert.Single(list.RootElement.EnumerateArray()).GetProperty("public_key").GetString());
    }

    [Fact]
    public void JsonOutput_OfAWriteIsItsOutcome_REQ_CLI_031()
    {
        using var created = JsonDocument.Parse(Ok("interface", "create", "wg0", "--addresses", "10.8.0.1/24", "--output", "json").Out);
        Assert.Equal("wg0", created.RootElement.GetProperty("interface").GetProperty("name").GetString());
        Assert.False(created.RootElement.GetProperty("restarted").GetBoolean());

        using var restarted = JsonDocument.Parse(Ok("interface", "update", "wg0", "--mtu", "1380", "--output", "json").Out);
        Assert.True(restarted.RootElement.GetProperty("restarted").GetBoolean());

        using var byok = JsonDocument.Parse(Ok("peer", "add", "wg0", "--public-key", PeerA, "--allowed-ips", "10.8.0.2/32", "--output", "json").Out);
        Assert.Equal(["peer", "restarted"], byok.RootElement.EnumerateObject().Select(p => p.Name));

        using var generated = JsonDocument.Parse(Ok("peer", "add", "wg0", "--generate-keypair", "--generate-preshared-key", "--output", "json").Out);
        Assert.Equal(["peer", "restarted", "private_key", "preshared_key", "client_configuration"], generated.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Contains(generated.RootElement.GetProperty("private_key").GetString()!, generated.RootElement.GetProperty("client_configuration").GetString());
    }

    [Fact]
    public void PeerAdd_Generated_PrintsTheFileAloneOnStandardOutput_REQ_CLI_032()
    {
        Ok("interface", "create", "wg0", "--addresses", "10.8.0.1/24");

        var added = Ok("peer", "add", "wg0", "--generate-keypair");

        // Nothing but the file, line for line.
        Assert.Equal(["[Interface]", "PrivateKey", "Address", "", "[Peer]", "PublicKey", "AllowedIPs", "Endpoint", "PersistentKeepalive", ""],
            added.Out.Split('\n').Select(line => line.Contains(" = ") ? line[..line.IndexOf(" = ")] : line));
        Assert.Equal("vpn.example.net:51820", Ini.Parse(added.Out).Peers[0]["Endpoint"]);
        var publicKey = Assert.Single(Stored.Interfaces["wg0"].Peers.Keys);
        Assert.Contains(publicKey, added.Error);
    }

    [Fact]
    public void Version_PrintsVersionAndCommit_REQ_CLI_033()
    {
        using var version = JsonDocument.Parse(Ok("version", "--output", "json").Out);
        Assert.Equal(["version", "commit"], version.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Matches(@"^\d+\.\d+\.\d+", version.RootElement.GetProperty("version").GetString());
        Assert.Contains(version.RootElement.GetProperty("commit").GetString()!, Ok("version").Out);
    }

    // ---- SPEC-09 section 2, through the command line

    [Fact]
    public void ConfigArgument_NamesTheFileTheCliReads_REQ_CFG_039()
    {
        var file = Path.Combine(_dir, "wg-agent");
        var store = Path.Combine(_dir, "from-file.json");
        File.WriteAllText(file, $"WG_AGENT_STATE_PATH={store}\n");

        var created = RunAs(["interface", "create", "wg0", "--addresses", "10.8.0.1/24", "--config", file]);

        Assert.Equal(0, created.Code);
        Assert.True(File.Exists(store));
    }

    [Fact]
    public void KeyFlag_OverridesTheEnvironment_REQ_CFG_042()
    {
        _environment["WG_AGENT_STATE_PATH"] = Path.Combine(_dir, "from-environment.json");
        Ok("interface", "create", "wg0", "--addresses", "10.8.0.1/24");   // Run() passes --state-path

        Assert.True(File.Exists(StorePath));
        Assert.False(File.Exists(Path.Combine(_dir, "from-environment.json")));
    }
}

using WgAgent.Cli.Configuration;

namespace WgAgent.Tests.Cli;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wg-agent-config-" + Guid.NewGuid().ToString("N"));
    private static readonly Dictionary<string, string> None = [];

    public ConfigurationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File(params string[] lines)
    {
        var path = Path.Combine(_dir, "wg-agent");
        System.IO.File.WriteAllLines(path, lines);
        return path;
    }

    private static AgentConfiguration Load(string? path, Dictionary<string, string>? environment = null, Dictionary<string, string>? flags = null) =>
        ConfigurationLoader.Load(path, environment ?? None, flags ?? None);

    private static string Refusal(Action load) => Assert.Throws<ConfigurationException>(load).Message;

    [Fact]
    public void File_OfKeyValueLines_AtTheDefaultPath_REQ_CFG_037()
    {
        Assert.Equal("/etc/default/wg-agent", ConfigurationLoader.DefaultPath);
        Assert.Equal("vpn.example.net", Load(File("WG_AGENT_NODE_ENDPOINT=vpn.example.net")).NodeEndpoint);
    }

    [Fact]
    public void ConfigArgument_OverridesThePath_REQ_CFG_039()
    {
        var other = Path.Combine(_dir, "other");
        System.IO.File.WriteAllText(other, "WG_AGENT_LOG_LEVEL=debug\n");
        Assert.Equal("debug", Load(other).LogLevel);
    }

    [Fact]
    public void AbsentFile_LeavesEveryDefault_REQ_CFG_040() =>
        Assert.Equal(Load(File()), Load(Path.Combine(_dir, "absent")));   // as an empty file: every default

    [Fact]
    public void EnvironmentVariable_OverridesTheFile_REQ_CFG_001()
    {
        var path = File("WG_AGENT_APPLY_TIMEOUT=20s");
        Assert.Equal(TimeSpan.FromSeconds(30), Load(path, new() { ["WG_AGENT_APPLY_TIMEOUT"] = "30s" }).ApplyTimeout);
    }

    [Fact]
    public void EnvironmentName_IsThePathUppercasedWithUnderscores_REQ_CFG_041()
    {
        string Name(string path) => ConfigurationLoader.Keys.Single(k => k.Path == path).EnvironmentName;
        Assert.Equal("WG_AGENT_LISTEN_ADDRESS", Name("listen.address"));
        Assert.Equal("WG_AGENT_APPLY_TIMEOUT", Name("apply_timeout"));
        Assert.Equal("WG_AGENT_PEER_ONLINE_THRESHOLD", Name("peer_online_threshold"));
    }

    [Fact]
    public void UnrecognizedKey_IsRefused_REQ_CFG_002()
    {
        Assert.Contains("WG_AGENT_LISTEN_ADRESS", Refusal(() => Load(File("WG_AGENT_LISTEN_ADRESS=127.0.0.1:9585"))));
        Assert.Contains("WG_AGENT_BOGUS", Refusal(() => Load(null, new() { ["WG_AGENT_BOGUS"] = "1" })));
        Refusal(() => Load(File("WG_AGENT_LOG_LEVEL")));             // not KEY=VALUE
        Refusal(() => Load(File("log.level=debug")));                // the dotted path is not the file's name for it
        Assert.Equal("info", Load(null, new() { ["PATH"] = "/usr/bin", ["WG_AGENTX"] = "1" }).LogLevel);   // not the agent's
    }

    [Fact]
    public void Flag_TakesPrecedenceOverFileAndEnvironment_REQ_CFG_042()
    {
        var path = File("WG_AGENT_APPLY_TIMEOUT=20s");
        var environment = new Dictionary<string, string> { ["WG_AGENT_APPLY_TIMEOUT"] = "30s" };
        Assert.Equal(TimeSpan.FromSeconds(40), Load(path, environment, new() { ["apply_timeout"] = "40s" }).ApplyTimeout);
    }

    [Fact]
    public void EveryKey_HasAFlagNamedAfterIt_REQ_CFG_050() =>
        Assert.Equal(
            ["--node-endpoint", "--listen-address", "--token-file", "--state-path", "--apply-timeout", "--peer-online-threshold", "--log-level"],
            ConfigurationLoader.Keys.Select(k => k.FlagName));

    [Fact]
    public void Keys_AreTheTablesAtItsDefaults_REQ_CFG_046()
    {
        Assert.Equal(["node.endpoint", "listen.address", "token.file", "state.path", "apply_timeout", "peer_online_threshold", "log.level"],
            ConfigurationLoader.Keys.Select(k => k.Path));
        var defaults = Load(Path.Combine(_dir, "absent"));
        Assert.Null(defaults.NodeEndpoint);
        Assert.Equal("127.0.0.1:9585", defaults.ListenAddress);
        Assert.Equal("/etc/wg-agent/token", defaults.TokenFile);
        Assert.Equal("/var/lib/wg-agent/state.json", defaults.StatePath);
        Assert.Equal(TimeSpan.FromSeconds(10), defaults.ApplyTimeout);
        Assert.Equal(TimeSpan.FromSeconds(180), defaults.PeerOnlineThreshold);
        Assert.Equal("info", defaults.LogLevel);
    }

    [Theory]
    [InlineData("15s", true)]
    [InlineData("1s", true)]
    [InlineData("0s", false)]
    [InlineData("1.5s", false)]
    [InlineData("15", false)]
    [InlineData("15m", false)]
    [InlineData("-5s", false)]
    [InlineData(" 15s", false)]
    [InlineData("s", false)]
    public void Duration_IsWholeSecondsFollowedByS_REQ_CFG_047(string value, bool accepted)
    {
        var flags = new Dictionary<string, string> { ["peer_online_threshold"] = value };
        if (accepted) Assert.Equal(TimeSpan.FromSeconds(int.Parse(value[..^1])), Load(null, flags: flags).PeerOnlineThreshold);
        else Refusal(() => Load(null, flags: flags));
    }

    [Fact]
    public void ValueItsKeyCannotHold_IsRefusedNamingTheKey_REQ_CFG_048()
    {
        Assert.Contains("log.level", Refusal(() => Load(File("WG_AGENT_LOG_LEVEL=verbose"))));
        Assert.Contains("listen.address", Refusal(() => Load(null, new() { ["WG_AGENT_LISTEN_ADDRESS"] = "localhost:9585" })));
        Assert.Contains("state.path", Refusal(() => Load(null, flags: new() { ["state.path"] = "state.json" })));
        Assert.Contains("node.endpoint", Refusal(() => Load(File("WG_AGENT_NODE_ENDPOINT=vpn.example.net:51820"))));
        Assert.Null(Load(File("WG_AGENT_NODE_ENDPOINT=")).NodeEndpoint);   // empty is none
    }

    [Fact]
    public void BlankAndCommentLines_AreIgnored_REQ_CFG_049()
    {
        var path = File("# every key, commented out", "", "   ", "#WG_AGENT_LOG_LEVEL=error", "WG_AGENT_LOG_LEVEL=debug");
        Assert.Equal("debug", Load(path).LogLevel);
        Refusal(() => Load(File("WG_AGENT_LOG_LEVEL = debug")));             // read as written
        Refusal(() => Load(File("WG_AGENT_LOG_LEVEL=\"debug\"")));
    }
}

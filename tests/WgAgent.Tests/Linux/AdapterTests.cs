using System.Diagnostics;
using WgAgent.Platform;
using WgAgent.Platform.Linux;

namespace WgAgent.Tests.Linux;

public sealed class AdapterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wg-agent-etc-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    /// <summary>Records what an adapter would start, and answers with canned output.</summary>
    private sealed class RecordingRunner(string output = "") : IProcessRunner
    {
        public List<(string Program, IReadOnlyList<string> Arguments, string? Input)> Runs { get; } = [];
        public ProcessResult Run(string program, IReadOnlyList<string> arguments, string? standardInput = null)
        {
            Runs.Add((program, arguments, standardInput));
            return new ProcessResult(0, output, "");
        }
    }

    [Fact]
    public void OnlyWgAndSystemctl_MayBeStarted_REQ_SEC_087()
    {
        Assert.Equal(["systemctl", "wg"], ProcessRunner.AgentPrograms.Keys.Order());
        Assert.All(ProcessRunner.AgentPrograms.Values, path => Assert.StartsWith("/", path));
        var runner = new ProcessRunner(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => runner.Run("ip", ["link"]));
        Assert.Throws<InvalidOperationException>(() => runner.Run("sh", ["-c", "true"]));
        Assert.Throws<InvalidOperationException>(() => runner.Run("wg-quick", ["up", "wg0"]));
    }

    [Fact]
    public void Arguments_ReachTheProgramVerbatim_NeverAShell_REQ_SEC_088()
    {
        var runner = new ProcessRunner(new Dictionary<string, string> { ["printf"] = "/usr/bin/printf" }, TimeSpan.FromSeconds(5));
        var result = runner.Run("printf", ["%s|%s", "a b", "$(id); rm -rf / `whoami`"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("a b|$(id); rm -rf / `whoami`", result.StandardOutput);
    }

    [Fact]
    public void Programs_StopAtTheChangesDeadline_REQ_API_022()
    {
        var deadline = new ApplyDeadline();
        var runner = new ProcessRunner(new Dictionary<string, string> { ["sleep"] = "/usr/bin/sleep" }, TimeSpan.FromSeconds(30), deadline);
        deadline.Begin(TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();

        Assert.Throws<PlatformException>(() => runner.Run("sleep", ["5"]));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"stopped after {clock.Elapsed}");
        Assert.Throws<PlatformException>(() => runner.Run("sleep", ["0"]));   // no time left: not started at all

        deadline.End();
        Assert.Equal(0, runner.Run("sleep", ["0"]).ExitCode);   // outside a change, the program's own bound
    }

    [Fact]
    public void Keys_TravelOnStandardInputOnly_REQ_SEC_089()
    {
        var secret = TestKeys.Secret(0x5A);
        var runner = new RecordingRunner(TestKeys.Base64(0x6B) + "\n");
        var wg = new WgTool(runner);

        Assert.Equal(TestKeys.Base64(0x6B), wg.PublicKeyOf(secret).ToString());
        var config = $"[Interface]\nPrivateKey = {secret.Reveal()}\nListenPort = 51820\n";
        wg.SyncConfig("wg0", config);

        Assert.Equal(["pubkey"], runner.Runs[0].Arguments);
        Assert.Contains(secret.Reveal(), runner.Runs[0].Input);
        Assert.Equal(["syncconf", "wg0", "/dev/stdin"], runner.Runs[1].Arguments);
        Assert.Equal(config, runner.Runs[1].Input);
        Assert.DoesNotContain(runner.Runs, r => r.Arguments.Any(a => a.Contains(secret.Reveal())));
    }

    [Fact]
    public void InterfaceFiles_AreRootOnlyAndReplacedAtomically_REQ_APL_009()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "wg0.conf");
        File.WriteAllText(path, "old");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        new WireGuardDirectory(_dir).Write("wg0", "[Interface]\n");

        Assert.Equal("[Interface]\n", File.ReadAllText(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(Run("id", "-u"), Run("stat", "-c", "%u", path));
        Assert.Equal(["wg0.conf"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Dump_NeverHandshakedIsNullAndNoneIsAbsent_REQ_RES_023()
    {
        var dump = string.Join('\n',
            $"{TestKeys.Base64(1)}\t{TestKeys.Base64(2)}\t51820\toff",
            $"{TestKeys.Base64(3)}\t(none)\t(none)\t10.8.0.2/32\t0\t0\t0\toff",
            $"{TestKeys.Base64(4)}\t{TestKeys.Base64(5)}\t203.0.113.9:40001\t10.8.0.3/32,192.168.50.0/24\t1790690813\t1024\t2048\t25") + "\n";
        var device = DumpParser.Parse(dump);
        Assert.Equal(TestKeys.Base64(2), device.PublicKey.ToString());
        Assert.Equal(51820u, device.ListenPort);
        Assert.Null(device.Peers[0].LatestHandshake);
        Assert.Null(device.Peers[0].Endpoint);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790690813), device.Peers[1].LatestHandshake);
        Assert.Equal(["10.8.0.3/32", "192.168.50.0/24"], device.Peers[1].AllowedIps);
        Assert.Equal(2048, device.Peers[1].TxBytes);
    }

    [Fact]
    public void TheUnit_IsWgQuickAtTheInterface_REQ_APL_001()
    {
        var runner = new RecordingRunner();
        var units = new SystemdUnits(runner);
        units.Enable("wg0");
        units.Start("wg0");
        Assert.All(runner.Runs, r => Assert.Equal("systemctl", r.Program));
        Assert.Equal(["enable", "--quiet", "wg-quick@wg0.service"], runner.Runs[0].Arguments);
        Assert.Equal(["start", "--quiet", "wg-quick@wg0.service"], runner.Runs[1].Arguments);
    }

    private static string Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }
}

namespace WgAgent.Platform.Linux;

/// <summary><see cref="IWireGuardTool"/> through the <c>wg</c> program.</summary>
public sealed class WgTool : IWireGuardTool
{
    private readonly IProcessRunner _runner;

    public WgTool(TimeSpan timeout, ApplyDeadline? deadline = null) : this(new ProcessRunner(timeout, deadline)) { }
    internal WgTool(IProcessRunner runner) => _runner = runner;

    public SecretKey GenerateKey() => ParseSecret(Wg(["genkey"]), "genkey");
    public SecretKey GeneratePresharedKey() => ParseSecret(Wg(["genpsk"]), "genpsk");

    public PublicKey PublicKeyOf(SecretKey privateKey) =>
        PublicKey.TryParse(Wg(["pubkey"], privateKey.Reveal() + "\n").Trim(), out var key)
            ? key : throw new PlatformException("wg pubkey returned no key.");

    public void SyncConfig(string interfaceName, string wireGuardConfig) =>
        Wg(["syncconf", interfaceName, "/dev/stdin"], wireGuardConfig);

    public DeviceDump? Show(string interfaceName)
    {
        var result = _runner.Run("wg", ["show", interfaceName, "dump"]);
        if (result.ExitCode != 0)
        {
            if (result.StandardError.Contains("No such device", StringComparison.Ordinal)) return null;
            throw new PlatformException($"wg show {interfaceName} dump: {result.StandardError.Trim()}");
        }
        return DumpParser.Parse(result.StandardOutput);
    }

    public IReadOnlyDictionary<string, uint> ListenPorts()
    {
        var ports = new Dictionary<string, uint>();
        foreach (var line in Wg(["show", "all", "listen-port"]).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length == 2 && uint.TryParse(fields[1], out var port)) ports[fields[0]] = port;
        }
        return ports;
    }

    private string Wg(IReadOnlyList<string> arguments, string? standardInput = null)
    {
        var result = _runner.Run("wg", arguments, standardInput);
        if (result.ExitCode != 0)
            throw new PlatformException($"wg {arguments[0]}: {result.StandardError.Trim()}");
        return result.StandardOutput;
    }

    private static SecretKey ParseSecret(string output, string command)
    {
        var key = SecretKey.Parse(output.Trim());
        return key.IsWellFormed ? key : throw new PlatformException($"wg {command} returned no key.");
    }
}

/// <summary>
/// Reads <c>wg show &lt;name&gt; dump</c>: a line for the device, then one per peer, tab-separated.
/// The dump carries the private and preshared keys; they are skipped, never kept.
/// </summary>
internal static class DumpParser
{
    public static DeviceDump Parse(string dump)
    {
        var lines = dump.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) throw new PlatformException("wg show dump returned nothing.");
        var device = lines[0].Split('\t');   // private-key, public-key, listen-port, fwmark
        if (device.Length < 3 || !PublicKey.TryParse(device[1], out var publicKey) || !uint.TryParse(device[2], out var port))
            throw new PlatformException("wg show dump: unexpected device line.");

        var peers = new List<PeerDump>();
        foreach (var line in lines.Skip(1))
        {
            // public-key, preshared-key, endpoint, allowed-ips, latest-handshake, rx, tx, keepalive
            var f = line.Split('\t');
            if (f.Length < 8 || !PublicKey.TryParse(f[0], out var peerKey))
                throw new PlatformException("wg show dump: unexpected peer line.");
            var handshake = long.Parse(f[4]);
            peers.Add(new PeerDump(
                peerKey,
                f[2] == "(none)" ? null : f[2],
                f[3] == "(none)" ? [] : f[3].Split(','),
                handshake == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(handshake),
                long.Parse(f[5]),
                long.Parse(f[6])));
        }
        return new DeviceDump(publicKey, port, peers);
    }
}

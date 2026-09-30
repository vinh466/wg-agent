using System.Security.Cryptography;
using WgAgent.Platform;

namespace WgAgent.Testing;

/// <summary>
/// A host in memory, behaving as the measurements of ADR-0013 found the real one: starting a unit
/// brings the device up from its file and runs PostUp; stopping runs the PostDown of the file as it
/// is at that moment and removes the device; <c>wg syncconf</c> changes only what differs and keeps
/// every other peer's session; a new private key ends every session. Every call is logged in order,
/// and any call can be made to fail.
/// </summary>
public sealed class FakeHost : IWireGuardTool, IUnitManager, IConfigDirectory, IHostNetwork
{
    public Dictionary<string, string> Files { get; } = [];
    public HashSet<string> Enabled { get; } = [];
    public HashSet<string> Active { get; } = [];
    public Dictionary<string, FakeDevice> Devices { get; } = [];
    public HashSet<string> OtherLinks { get; } = [];
    public Dictionary<string, List<Cidr>> OtherAddresses { get; } = [];
    public Dictionary<string, uint> ForeignListenPorts { get; } = [];

    /// <summary>Every operation, in order: "write wg0", "stop wg0", "sync wg0"...</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Every hook command wg-quick ran, "%i" replaced.</summary>
    public List<string> Hooks { get; } = [];

    /// <summary>The names whose file was read.</summary>
    public HashSet<string> Reads { get; } = [];

    /// <summary>Operations to fail once, by their log entry: "start wg0" makes the next start throw.</summary>
    public HashSet<string> Failures { get; } = [];

    private int _keys;

    private void Log(string entry)
    {
        Calls.Add(entry);
        if (Failures.Remove(entry)) throw new PlatformException($"{entry} failed (injected)");
    }

    // ---- IWireGuardTool

    public SecretKey GenerateKey() => SecretKey.FromBytes(Enumerable.Repeat((byte)(0x40 + ++_keys), 32).ToArray());
    public SecretKey GeneratePresharedKey() => SecretKey.FromBytes(Enumerable.Repeat((byte)(0x80 + ++_keys), 32).ToArray());

    /// <summary>Not X25519: a stable function of the private key is all a test needs.</summary>
    public PublicKey PublicKeyOf(SecretKey privateKey) =>
        PublicKey.FromBytes(SHA256.HashData(Convert.FromBase64String(privateKey.Reveal())));

    public void SyncConfig(string interfaceName, string wireGuardConfig)
    {
        Log($"sync {interfaceName}");
        if (!Devices.TryGetValue(interfaceName, out var device))
            throw new PlatformException($"Unable to modify interface {interfaceName}: No such device");
        var config = Ini.Parse(wireGuardConfig);
        if (config.Interface.ContainsKey("Address") || config.Interface.ContainsKey("MTU") || config.Interface.ContainsKey("PostUp"))
            throw new PlatformException("wg syncconf: Line unrecognized: a wg-quick key");
        var newKey = config.Interface["PrivateKey"];
        var keyChanged = newKey != device.PrivateKey;
        device.PrivateKey = newKey;
        device.ListenPort = uint.Parse(config.Interface["ListenPort"]);
        foreach (var gone in device.Peers.Keys.Except(config.Peers.Select(p => p["PublicKey"])).ToList())
            device.Peers.Remove(gone);
        foreach (var section in config.Peers)
        {
            var key = section["PublicKey"];
            if (!device.Peers.TryGetValue(key, out var peer)) device.Peers[key] = peer = new FakePeer();
            else if (keyChanged) peer.Session++;
            peer.Configure(section);
        }
    }

    public DeviceDump? Show(string interfaceName)
    {
        Calls.Add($"show {interfaceName}");
        if (!Devices.TryGetValue(interfaceName, out var device)) return null;
        return new DeviceDump(
            PublicKeyOf(SecretKey.Parse(device.PrivateKey)),
            device.ListenPort,
            [.. device.Peers.Select(p => new PeerDump(PublicKey.TryParse(p.Key, out var k) ? k : throw new InvalidOperationException(),
                p.Value.Endpoint, p.Value.AllowedIps, p.Value.LatestHandshake, p.Value.RxBytes, p.Value.TxBytes))]);
    }

    public IReadOnlyDictionary<string, uint> ListenPorts() =>
        Devices.ToDictionary(d => d.Key, d => d.Value.ListenPort).Concat(ForeignListenPorts).ToDictionary(p => p.Key, p => p.Value);

    // ---- IUnitManager

    public void Enable(string interfaceName) { Log($"enable {interfaceName}"); Enabled.Add(interfaceName); }
    public void Disable(string interfaceName) { Log($"disable {interfaceName}"); Enabled.Remove(interfaceName); }

    public void Start(string interfaceName)
    {
        Log($"start {interfaceName}");
        if (Active.Contains(interfaceName)) return;
        if (!Files.TryGetValue(interfaceName, out var text))
            throw new PlatformException($"wg-quick: /etc/wireguard/{interfaceName}.conf does not exist");
        var config = Ini.Parse(text);
        var device = new FakeDevice
        {
            PrivateKey = config.Interface["PrivateKey"],
            ListenPort = uint.Parse(config.Interface["ListenPort"]),
            Addresses = [.. config.Interface["Address"].Split(',', StringSplitOptions.TrimEntries).Select(a => Cidr.TryParse(a, out var c) ? c : default)],
        };
        foreach (var section in config.Peers)
        {
            var peer = new FakePeer();
            peer.Configure(section);
            device.Peers[section["PublicKey"]] = peer;
        }
        Devices[interfaceName] = device;
        foreach (var command in config.InterfaceAll("PostUp")) Hooks.Add("up: " + command.Replace("%i", interfaceName));
        Active.Add(interfaceName);
    }

    public void Stop(string interfaceName)
    {
        Log($"stop {interfaceName}");
        if (!Active.Remove(interfaceName)) return;
        // wg-quick down reads the file as it is now.
        if (Files.TryGetValue(interfaceName, out var text))
            foreach (var command in Ini.Parse(text).InterfaceAll("PostDown")) Hooks.Add("down: " + command.Replace("%i", interfaceName));
        Devices.Remove(interfaceName);
    }

    public bool IsActive(string interfaceName) => Active.Contains(interfaceName);

    // ---- IConfigDirectory

    public IReadOnlySet<string> Names() => Files.Keys.ToHashSet();

    public string? Read(string interfaceName)
    {
        Reads.Add(interfaceName);
        return Files.GetValueOrDefault(interfaceName);
    }

    public void Write(string interfaceName, string content) { Log($"write {interfaceName}"); Files[interfaceName] = content; }
    public void Delete(string interfaceName) { Log($"delete {interfaceName}"); Files.Remove(interfaceName); }

    // ---- IHostNetwork

    public IReadOnlySet<string> LinkNames() => Devices.Keys.Concat(OtherLinks).ToHashSet();

    public IReadOnlyList<Cidr> AddressesOf(string linkName) =>
        Devices.TryGetValue(linkName, out var device) ? device.Addresses : OtherAddresses.GetValueOrDefault(linkName) ?? [];
}

public sealed class FakeDevice
{
    public required string PrivateKey { get; set; }
    public uint ListenPort { get; set; }
    public List<Cidr> Addresses { get; init; } = [];
    public Dictionary<string, FakePeer> Peers { get; } = [];
}

public sealed class FakePeer
{
    private static int _sessions;

    /// <summary>Changes whenever the peer's session is lost; unchanged means undisturbed.</summary>
    public int Session { get; set; } = Interlocked.Increment(ref _sessions);

    public List<string> AllowedIps { get; private set; } = [];
    public string? Endpoint { get; set; }
    public DateTimeOffset? LatestHandshake { get; set; }
    public long RxBytes { get; set; }
    public long TxBytes { get; set; }

    internal void Configure(IReadOnlyDictionary<string, string> section)
    {
        AllowedIps = [.. section.GetValueOrDefault("AllowedIPs", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
        if (section.TryGetValue("Endpoint", out var endpoint)) Endpoint = endpoint;
    }
}

/// <summary>Just enough of the wg-quick file format: sections, "Key = Value" lines, repeated keys.</summary>
public sealed class Ini
{
    public Dictionary<string, string> Interface { get; } = [];
    public List<(string Key, string Value)> InterfaceLines { get; } = [];
    public List<Dictionary<string, string>> Peers { get; } = [];

    public IEnumerable<string> InterfaceAll(string key) => InterfaceLines.Where(l => l.Key == key).Select(l => l.Value);

    public static Ini Parse(string text)
    {
        var ini = new Ini();
        Dictionary<string, string>? section = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line == "[Interface]") { section = ini.Interface; continue; }
            if (line == "[Peer]") { section = []; ini.Peers.Add(section); continue; }
            var equals = line.IndexOf('=');
            if (equals < 0 || section is null) throw new FormatException($"Unrecognised line: {line}");
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            section[key] = value;
            if (ReferenceEquals(section, ini.Interface)) ini.InterfaceLines.Add((key, value));
        }
        return ini;
    }
}

/// <summary>A clock a test sets.</summary>
public sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

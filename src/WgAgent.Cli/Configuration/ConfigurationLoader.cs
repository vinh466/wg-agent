using System.Text.RegularExpressions;
using WgAgent.Core.Validation;
using WgAgent.Platform;

namespace WgAgent.Cli.Configuration;

/// <summary>A configuration the agent refuses to start with (REQ-CFG-002, REQ-CFG-048).</summary>
public sealed class ConfigurationException(string message) : Exception(message);

/// <summary>One key of REQ-CFG-046: its dotted path, its default, and what it can hold.</summary>
public sealed partial record ConfigKey(string Path, string Default, string Holds, Func<string, bool> Accepts)
{
    /// <summary>REQ-CFG-041: the dotted path uppercased, every other character an underscore.</summary>
    public string Upper => NotUpperOrDigit().Replace(Path.ToUpperInvariant(), "_");

    /// <summary>REQ-CFG-001</summary>
    public string EnvironmentName => ConfigurationLoader.Prefix + Upper;

    /// <summary>REQ-CFG-050</summary>
    public string FlagName => "--" + Upper.ToLowerInvariant().Replace('_', '-');

    [GeneratedRegex("[^A-Z0-9]")]
    private static partial Regex NotUpperOrDigit();
}

/// <summary>The settings of SPEC-09 section 2, every key at its value.</summary>
public sealed record AgentConfiguration
{
    public string? NodeEndpoint { get; init; }
    public required string ListenAddress { get; init; }
    public required string TokenFile { get; init; }
    public required string StatePath { get; init; }
    public required TimeSpan ApplyTimeout { get; init; }
    public required TimeSpan PeerOnlineThreshold { get; init; }
    public required string LogLevel { get; init; }
}

/// <summary>
/// Reads the configuration in the order of REQ-CFG-042 — the defaults, then the file, then the
/// environment, then the flags — refusing an unrecognized key (REQ-CFG-002) and a value its key
/// cannot hold (REQ-CFG-048) wherever either appears.
/// </summary>
public static partial class ConfigurationLoader
{
    /// <summary>REQ-CFG-037</summary>
    public const string DefaultPath = "/etc/default/wg-agent";

    public const string Prefix = "WG_AGENT_";

    public static readonly IReadOnlyList<ConfigKey> Keys =
    [
        new("node.endpoint", "", "a host, or empty", value => value.Length == 0 || PeerRules.IsHost(value)),
        new("listen.address", "127.0.0.1:9585", "an IPv4 address and a port", IsIPv4AddressAndPort),
        new("token.file", "/etc/wg-agent/token", "an absolute path", value => value.StartsWith('/')),
        new("state.path", "/var/lib/wg-agent/state.json", "an absolute path", value => value.StartsWith('/')),
        new("apply_timeout", "10s", "a whole number of seconds greater than zero followed by s", IsDuration),
        new("peer_online_threshold", "180s", "a whole number of seconds greater than zero followed by s", IsDuration),
        new("log.level", "info", "debug, info, warn or error", value => value is "debug" or "info" or "warn" or "error"),
    ];

    /// <param name="path">The file named by <c>--config</c> (REQ-CFG-039), or null for the default.</param>
    /// <param name="environment">The process environment.</param>
    /// <param name="flags">The values flags gave, by the dotted path of their key.</param>
    public static AgentConfiguration Load(string? path, IReadOnlyDictionary<string, string> environment, IReadOnlyDictionary<string, string> flags)
    {
        var values = Keys.ToDictionary(k => k.Path, k => k.Default);

        var file = path ?? DefaultPath;
        if (File.Exists(file))   // REQ-CFG-040: an absent file leaves the defaults
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;   // REQ-CFG-049
                var equals = line.IndexOf('=');
                var key = equals > 0 ? ByEnvironmentName(line[..equals]) : null;
                if (key is null)
                    throw new ConfigurationException($"{file}:{i + 1}: '{line}' does not set a configuration key.");
                Set(values, key, line[(equals + 1)..], $"{file}:{i + 1}");
            }
        }

        foreach (var (name, value) in environment.Where(e => e.Key.StartsWith(Prefix, StringComparison.Ordinal)).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var key = ByEnvironmentName(name)
                ?? throw new ConfigurationException($"The environment variable {name} names no configuration key.");
            Set(values, key, value, $"the environment variable {name}");
        }

        foreach (var (keyPath, value) in flags)
        {
            var key = Keys.Single(k => k.Path == keyPath);
            Set(values, key, value, $"the flag {key.FlagName}");
        }

        return new AgentConfiguration
        {
            NodeEndpoint = values["node.endpoint"] is { Length: > 0 } host ? host : null,
            ListenAddress = values["listen.address"],
            TokenFile = values["token.file"],
            StatePath = values["state.path"],
            ApplyTimeout = Duration(values["apply_timeout"]),
            PeerOnlineThreshold = Duration(values["peer_online_threshold"]),
            LogLevel = values["log.level"],
        };
    }

    private static ConfigKey? ByEnvironmentName(string name) => Keys.FirstOrDefault(k => k.EnvironmentName == name);

    private static void Set(Dictionary<string, string> values, ConfigKey key, string value, string source)
    {
        if (!key.Accepts(value))
            throw new ConfigurationException($"{source}: {key.Path} is '{value}', which is not {key.Holds}.");
        values[key.Path] = value;
    }

    /// <summary>REQ-CFG-047</summary>
    private static bool IsDuration(string value) =>
        SecondsPattern().IsMatch(value) && uint.TryParse(value[..^1], out var seconds) && seconds > 0;

    private static TimeSpan Duration(string value) => TimeSpan.FromSeconds(uint.Parse(value[..^1]));

    private static bool IsIPv4AddressAndPort(string value)
    {
        var colon = value.LastIndexOf(':');
        if (colon <= 0) return false;
        var port = value[(colon + 1)..];
        return Cidr.TryParse(value[..colon] + "/32", out var address) && !address.IsIPv6
            && PortPattern().IsMatch(port) && ushort.TryParse(port, out var number) && number > 0;
    }

    [GeneratedRegex(@"^[0-9]+s$")]
    private static partial Regex SecondsPattern();

    [GeneratedRegex(@"^[0-9]{1,5}$")]
    private static partial Regex PortPattern();
}

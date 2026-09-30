using System.CommandLine;
using System.Globalization;
using WgAgent.Core.Model;

namespace WgAgent.Cli;

/// <summary>A command line the parser accepts and the command cannot: a port that is not a number.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>
/// Flags named after the fields they set, given once per list or map entry (REQ-CLI-026). An empty
/// value contributes no entry and leaves a scalar without a value (REQ-CLI-029). No flag carries a
/// private or preshared key (REQ-CLI-028).
/// </summary>
internal static class Flags
{
    public static string NameOf(string field) => "--" + field.Replace('_', '-');

    public static Option<string[]> List(string field, string description) => new(NameOf(field)) { Description = description };
    public static Option<string> Scalar(string field, string description) => new(NameOf(field)) { Description = description };
    public static Option<bool> Switch(string field, string description) => new(NameOf(field)) { Description = description };

    public static bool Given(ParseResult parse, Option option) => parse.GetResult(option) is { Implicit: false };

    public static IReadOnlyList<string> Entries(ParseResult parse, Option<string[]> option) =>
        [.. (parse.GetValue(option) ?? []).Where(value => value.Length > 0)];

    public static IReadOnlyDictionary<string, string> Map(ParseResult parse, Option<string[]> option)
    {
        var map = new Dictionary<string, string>();
        foreach (var entry in Entries(parse, option))
        {
            var equals = entry.IndexOf('=');
            if (equals <= 0) throw new UsageException($"{option.Name} takes key=value, not '{entry}'.");
            map[entry[..equals]] = entry[(equals + 1)..];
        }
        return map;
    }

    public static string? Text(ParseResult parse, Option<string> option) =>
        parse.GetValue(option) is { Length: > 0 } value ? value : null;

    public static uint? Number(ParseResult parse, Option<string> option) => Text(parse, option) switch
    {
        null => null,
        var text when uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) => number,
        var text => throw new UsageException($"{option.Name} takes a whole number, not '{text}'."),
    };

    public static bool? Boolean(ParseResult parse, Option<string> option) => Text(parse, option) switch
    {
        null => null,
        "true" => true,
        "false" => false,
        var text => throw new UsageException($"{option.Name} takes true or false, not '{text}'."),
    };
}

/// <summary>The flags of the InterfaceSpec fields a command line may set.</summary>
internal sealed class InterfaceFields
{
    public readonly Option<string[]> Addresses = Flags.List("addresses", "An address of the interface, as a CIDR; once per address.");
    public readonly Option<string> ListenPort = Flags.Scalar("listen_port", "The UDP port the interface listens on.");
    public readonly Option<string> Mtu = Flags.Scalar("mtu", "The interface's MTU.");
    public readonly Option<string> Enabled = Flags.Scalar("enabled", "true or false: whether the interface's unit runs.");
    public readonly Option<string[]> Labels = Flags.List("labels", "A label, as key=value; once per label.");
    public readonly Option<string[]> PostUp = Flags.List("post_up", "A command wg-quick runs after bringing the interface up; once per command.");
    public readonly Option<string[]> PostDown = Flags.List("post_down", "A command wg-quick runs after taking the interface down; once per command.");

    private IEnumerable<Option> All => [Addresses, ListenPort, Mtu, Enabled, Labels, PostUp, PostDown];

    public void AddTo(Command command)
    {
        foreach (var option in All) command.Options.Add(option);
    }

    public bool AnyGiven(ParseResult parse) => All.Any(option => Flags.Given(parse, option));

    /// <summary>The fields the flags name, changed; the rest as they were (REQ-CLI-027).</summary>
    public InterfaceSpec Apply(ParseResult parse, InterfaceSpec spec)
    {
        if (Flags.Given(parse, Addresses)) spec = spec with { Addresses = Flags.Entries(parse, Addresses) };
        if (Flags.Given(parse, ListenPort)) spec = spec with { ListenPort = Flags.Number(parse, ListenPort) };
        if (Flags.Given(parse, Mtu)) spec = spec with { Mtu = Flags.Number(parse, Mtu) };
        if (Flags.Given(parse, Enabled)) spec = spec with { Enabled = Flags.Boolean(parse, Enabled) };
        if (Flags.Given(parse, Labels)) spec = spec with { Labels = Flags.Map(parse, Labels) };
        if (Flags.Given(parse, PostUp)) spec = spec with { PostUp = Flags.Entries(parse, PostUp) };
        if (Flags.Given(parse, PostDown)) spec = spec with { PostDown = Flags.Entries(parse, PostDown) };
        return spec;
    }
}

/// <summary>The flags of the PeerSpec fields a command line may set; never the preshared key.</summary>
internal sealed class PeerFields
{
    public readonly Option<string[]> AllowedIps = Flags.List("allowed_ips", "An allowed IP of the peer, as a CIDR; once per entry.");
    public readonly Option<string> Endpoint = Flags.Scalar("endpoint", "Where the node reaches the peer, as host:port.");
    public readonly Option<string> PersistentKeepalive = Flags.Scalar("persistent_keepalive", "Seconds between keepalives; 0 disables them.");
    public readonly Option<string[]> Labels = Flags.List("labels", "A label, as key=value; once per label.");

    public void AddTo(Command command)
    {
        foreach (var option in new Option[] { AllowedIps, Endpoint, PersistentKeepalive, Labels }) command.Options.Add(option);
    }

    /// <summary>The fields the flags name, changed; the rest as they were (REQ-CLI-027).</summary>
    public PeerSpec Apply(ParseResult parse, PeerSpec spec)
    {
        if (Flags.Given(parse, AllowedIps)) spec = spec with { AllowedIps = Flags.Entries(parse, AllowedIps) };
        if (Flags.Given(parse, Endpoint)) spec = spec with { Endpoint = Flags.Text(parse, Endpoint) };
        if (Flags.Given(parse, PersistentKeepalive)) spec = spec with { PersistentKeepalive = Flags.Number(parse, PersistentKeepalive) };
        if (Flags.Given(parse, Labels)) spec = spec with { Labels = Flags.Map(parse, Labels) };
        return spec;
    }
}

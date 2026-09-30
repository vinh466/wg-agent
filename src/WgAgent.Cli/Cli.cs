using System.CommandLine;
using System.Text.Json;
using WgAgent.Cli.Configuration;
using WgAgent.Core;
using WgAgent.Core.Store;
using WgAgent.Platform;
using WgAgent.Platform.Linux;
using WgAgent.Service;

namespace WgAgent.Cli;

/// <summary>What the CLI builds its service from: the Linux adapters, or a test's own.</summary>
public interface ICliHost
{
    HostPorts Ports(AgentConfiguration configuration);
}

/// <summary>The node itself: <c>wg</c>, <c>systemctl</c> and <c>/etc/wireguard/</c>.</summary>
public sealed class LinuxHost : ICliHost
{
    public HostPorts Ports(AgentConfiguration configuration)
    {
        // Each program is bounded by apply_timeout on its own, and by the change's deadline while
        // one is applied (REQ-API-022, REQ-APL-012).
        var deadline = new ApplyDeadline();
        return new HostPorts(
            new WgTool(configuration.ApplyTimeout, deadline),
            new SystemdUnits(configuration.ApplyTimeout, deadline),
            new WireGuardDirectory(),
            new HostNetwork(),
            deadline);
    }
}

public sealed record CliContext(ICliHost Host, TextWriter Out, TextWriter Error, IReadOnlyDictionary<string, string> Environment, TimeProvider Clock);

/// <summary>
/// The subcommands of SPEC-12. Each acts on the store and the interfaces directly (REQ-CLI-002),
/// through the operations the API shares (REQ-CLI-023).
/// </summary>
public static class Cli
{
    public static int Run(IReadOnlyList<string> args, CliContext context)
    {
        var global = new GlobalOptions();
        var root = new RootCommand("Manages the WireGuard interfaces of this node through wg and wg-quick.");

        // The parser's own extras — a --version option, directives, @file arguments — are surface no
        // requirement asks for; `version` is the subcommand of REQ-CLI-001.
        foreach (var extra in root.Options.OfType<VersionOption>().ToList()) root.Options.Remove(extra);
        root.Directives.Clear();

        global.AddTo(root);
        root.Subcommands.Add(InterfaceCommands.Build(context, global));
        root.Subcommands.Add(PeerCommands.Build(context, global));
        root.Subcommands.Add(VersionCommand.Build(context, global));
        var parse = root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });
        return parse.Invoke(new InvocationConfiguration { Output = context.Out, Error = context.Error });
    }

    /// <summary>
    /// Runs a subcommand's body; a failure exits non-zero with its reason on standard error
    /// (REQ-CLI-021), a refused operation led by its reason code.
    /// </summary>
    internal static void Act(Command command, CliContext context, GlobalOptions global, Action<Invocation> body)
    {
        command.SetAction(parse =>
        {
            try
            {
                body(new Invocation(parse, context, global));
                return 0;
            }
            catch (AgentException refused)
            {
                context.Error.WriteLine($"wg-agent: {refused.Code}: {refused.Message}");
                return 1;
            }
            catch (Exception failure) when (failure is ConfigurationException or UsageException or PlatformException
                                            or JsonException or IOException or UnauthorizedAccessException)
            {
                context.Error.WriteLine($"wg-agent: {failure.Message}");
                return 1;
            }
        });
    }
}

/// <summary>The options every subcommand takes: the configuration file, the output form, and a flag per key.</summary>
internal sealed class GlobalOptions
{
    /// <summary>REQ-CFG-039</summary>
    public readonly Option<string> Config = new("--config")
    {
        Description = $"The configuration file (default {ConfigurationLoader.DefaultPath}).",
        Recursive = true,
    };

    /// <summary>REQ-CLI-020</summary>
    public readonly Option<string> Output = new("--output")
    {
        Description = "text, or json for a script.",
        Recursive = true,
        DefaultValueFactory = _ => "text",
    };

    /// <summary>REQ-CFG-050, by the dotted path of the key each sets.</summary>
    public readonly IReadOnlyDictionary<string, Option<string>> Keys = ConfigurationLoader.Keys.ToDictionary(
        key => key.Path,
        key => new Option<string>(key.FlagName) { Description = $"The configuration key {key.Path}.", Recursive = true });

    public GlobalOptions() => Output.AcceptOnlyFromAmong("text", "json");

    public void AddTo(Command root)
    {
        root.Options.Add(Config);
        root.Options.Add(Output);
        foreach (var option in Keys.Values) root.Options.Add(option);
    }
}

/// <summary>One run of a subcommand: its parse, its configuration, and the service built from it.</summary>
internal sealed class Invocation(ParseResult parse, CliContext context, GlobalOptions global)
{
    private AgentService? _service;

    public ParseResult Parse => parse;
    public TextWriter Out => context.Out;
    public TextWriter Error => context.Error;
    public bool Json => parse.GetValue(global.Output) == "json";

    public AgentService Service => _service ??= Build();

    private AgentService Build()
    {
        var flags = global.Keys
            .Where(key => Flags.Given(parse, key.Value))
            .ToDictionary(key => key.Key, key => parse.GetValue(key.Value) ?? "");
        var configuration = ConfigurationLoader.Load(parse.GetValue(global.Config), context.Environment, flags);
        var options = new AgentOptions
        {
            ApplyTimeout = configuration.ApplyTimeout,
            PeerOnlineThreshold = configuration.PeerOnlineThreshold,
            NodeEndpoint = configuration.NodeEndpoint,
        };
        return new AgentService(new StateStore(configuration.StatePath), context.Host.Ports(configuration), options, context.Clock);
    }

    /// <summary>REQ-APL-007, on standard error so it never mixes with a file on standard output.</summary>
    public void Restarted(string interfaceName, bool restarted)
    {
        if (restarted) Error.WriteLine($"wg-agent: {interfaceName} was restarted, which interrupted its sessions.");
    }
}

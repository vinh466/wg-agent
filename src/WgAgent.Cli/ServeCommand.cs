using System.CommandLine;
using WgAgent.Api;
using WgAgent.Cli.Configuration;
using WgAgent.Core.Store;
using WgAgent.Platform.Linux;

namespace WgAgent.Cli;

/// <summary><c>serve</c> — run the agent and its API; the systemd unit invokes this (REQ-CLI-001).</summary>
internal static class ServeCommand
{
    public static Command Build(CliContext context, GlobalOptions global)
    {
        var command = new Command("serve", "Run the REST API listener until stopped.");
        // serve does not act on the store directly (REQ-CLI-002); it runs the listener, whose exit
        // code is the startup check's when one fails (REQ-API-050).
        command.SetAction(parse =>
        {
            try
            {
                var run = new Invocation(parse, context, global);
                var options = new ServerOptions
                {
                    Service = run.Service,
                    Store = new StateStore(run.Config.StatePath),
                    Token = new TokenFile(run.Config.TokenFile),
                    ListenAddress = run.Config.ListenAddress,
                    LogLevel = run.Config.LogLevel,
                    Clock = context.Clock,
                };
                return Server.Run(options, context.Error);
            }
            catch (ConfigurationException failure)
            {
                context.Error.WriteLine($"wg-agent: {failure.Message}");
                return 1;
            }
        });
        return command;
    }
}

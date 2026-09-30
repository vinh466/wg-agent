using System.CommandLine;
using System.Text.Json;
using WgAgent.Cli.Output;
using WgAgent.Platform;
using WgAgent.Platform.Linux;

namespace WgAgent.Cli;

/// <summary><c>token rotate</c> — the one command that issues a token (SPEC-12 section 4).</summary>
internal static class TokenCommands
{
    public static Command Build(CliContext context, GlobalOptions global)
    {
        var command = new Command("token", "Manage the API bearer token.");
        command.Subcommands.Add(Rotate(context, global));
        return command;
    }

    private static Command Rotate(CliContext context, GlobalOptions global)
    {
        var command = new Command("rotate", "Replace the token, and print the new one exactly once.");
        Cli.Act(command, context, global, run =>
        {
            // 256 bits from the kernel's generator through wg genpsk meets REQ-CLI-010.
            var token = Secret.From(run.Ports.Wg.GeneratePresharedKey().Reveal());
            new TokenFile(run.Config.TokenFile).Write(token);   // REQ-CLI-013, REQ-CLI-014

            if (run.Json) run.Out.WriteLine(JsonSerializer.Serialize(new TokenInfo { Token = token.Reveal() }, CliJsonContext.Default.TokenInfo));
            else run.Out.WriteLine(token.Reveal());   // REQ-CLI-011: printed once, by the command that generates it
        });
        return command;
    }
}

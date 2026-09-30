using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using WgAgent.Cli.Output;

namespace WgAgent.Cli;

/// <summary><c>version</c> — the version and the commit the binary was built from (REQ-CLI-033).</summary>
internal static class VersionCommand
{
    public static Command Build(CliContext context, GlobalOptions global)
    {
        var command = new Command("version", "Print the version and the commit.");
        Cli.Act(command, context, global, run =>
        {
            var info = Current();
            if (run.Json) run.Out.WriteLine(JsonSerializer.Serialize(info, CliJsonContext.Default.VersionInfo));
            else run.Out.WriteLine($"wg-agent {info.Version} (commit {info.Commit})");
        });
        return command;
    }

    /// <summary>The SDK writes the commit after a '+' in the informational version.</summary>
    public static VersionInfo Current()
    {
        var informational = typeof(VersionCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+');
        return plus < 0
            ? new VersionInfo { Version = informational, Commit = "unknown" }
            : new VersionInfo { Version = informational[..plus], Commit = informational[(plus + 1)..] };
    }
}

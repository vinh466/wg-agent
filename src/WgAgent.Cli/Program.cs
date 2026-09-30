using System.Collections;
using WgAgent.Cli;

var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
    .ToDictionary(entry => (string)entry.Key, entry => entry.Value as string ?? "");
return Cli.Run(args, new CliContext(new LinuxHost(), Console.Out, Console.Error, environment, TimeProvider.System));

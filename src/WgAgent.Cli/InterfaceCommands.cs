using System.CommandLine;
using System.Text.Json;
using WgAgent.Cli.Output;
using WgAgent.Core;
using WgAgent.Core.Json;
using WgAgent.Core.Model;
using WgAgent.Service;

namespace WgAgent.Cli;

/// <summary><c>interface create | list | get | update | delete</c> — REQ-CLI-001.</summary>
internal static class InterfaceCommands
{
    public static Command Build(CliContext context, GlobalOptions global)
    {
        var command = new Command("interface", "Create, read, change and delete the interfaces the agent manages.");
        command.Subcommands.Add(Create(context, global));
        command.Subcommands.Add(List(context, global));
        command.Subcommands.Add(Get(context, global));
        command.Subcommands.Add(Update(context, global));
        command.Subcommands.Add(Delete(context, global));
        return command;
    }

    private static Argument<string> Name() => new("name") { Description = "The interface's name." };

    private static Command Create(CliContext context, GlobalOptions global)
    {
        var name = Name();
        var fields = new InterfaceFields();
        var file = new Option<string>("--file") { Description = "A JSON document holding the interface's spec and its peers." };
        var command = new Command("create", "Create an interface, or an interface and its peers from a JSON document.") { name, file };
        fields.AddTo(command);
        Cli.Act(command, context, global, run =>
        {
            WriteResult<InterfaceResource> result;
            if (Flags.Given(run.Parse, file))
            {
                if (fields.AnyGiven(run.Parse))   // REQ-CLI-035
                    throw new UsageException("--file takes the whole spec; it cannot be combined with a flag setting a field of it.");
                var document = ReadDocument(run.Parse.GetValue(file)!);
                result = run.Service.CreateInterface(run.Parse.GetValue(name)!, document.Spec,
                    [.. (document.Peers ?? []).Select(peer => (peer.PublicKey, peer.Spec))]);   // REQ-CLI-025
            }
            else
            {
                result = run.Service.CreateInterface(run.Parse.GetValue(name)!, fields.Apply(run.Parse, new InterfaceSpec()));
            }
            Write(run, result);
        });
        return command;
    }

    private static Command List(CliContext context, GlobalOptions global)
    {
        var command = new Command("list", "List the interfaces the agent manages.");
        Cli.Act(command, context, global, run =>
        {
            var interfaces = run.Service.ListInterfaces();
            if (run.Json) run.Out.WriteLine(JsonSerializer.Serialize(interfaces, CoreJsonContext.Default.IReadOnlyListInterfaceResource));
            else TextOutput.Interfaces(run.Out, interfaces);
        });
        return command;
    }

    private static Command Get(CliContext context, GlobalOptions global)
    {
        var name = Name();
        var command = new Command("get", "Show an interface, its spec and its status.") { name };
        Cli.Act(command, context, global, run =>
        {
            var resource = run.Service.GetInterface(run.Parse.GetValue(name)!);
            if (run.Json) run.Out.WriteLine(JsonSerializer.Serialize(resource, CoreJsonContext.Default.InterfaceResource));
            else TextOutput.Interface(run.Out, resource);
        });
        return command;
    }

    private static Command Update(CliContext context, GlobalOptions global)
    {
        var name = Name();
        var fields = new InterfaceFields();
        var command = new Command("update", "Change the fields of an interface the flags name.") { name };
        fields.AddTo(command);
        Cli.Act(command, context, global, run =>
            Write(run, run.Service.UpdateInterface(run.Parse.GetValue(name)!, stored => fields.Apply(run.Parse, stored))));   // REQ-CLI-027
        return command;
    }

    private static Command Delete(CliContext context, GlobalOptions global)
    {
        var name = Name();
        var command = new Command("delete", "Delete an interface: stop its unit and remove its file and its peers.") { name };
        Cli.Act(command, context, global, run => run.Service.DeleteInterface(run.Parse.GetValue(name)!));
        return command;
    }

    private static void Write(Invocation run, WriteResult<InterfaceResource> result)
    {
        if (run.Json)
        {
            var outcome = new InterfaceOutcome { Interface = result.Resource, Restarted = result.Restarted };
            run.Out.WriteLine(JsonSerializer.Serialize(outcome, CliJsonContext.Default.InterfaceOutcome));
            return;
        }
        TextOutput.Interface(run.Out, result.Resource);
        run.Restarted(result.Resource.Name, result.Restarted);
    }

    /// <summary>REQ-CLI-034, refusing a member the document's schema does not define (REQ-VAL-050).</summary>
    private static InterfaceDocument ReadDocument(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        if (UnknownMembers.Find(json.RootElement, CliJsonContext.Default.InterfaceDocument) is { } member)
            throw new AgentException(ReasonCodes.FieldUnknown, $"The document carries '{member}', which its schema does not define.");
        return json.RootElement.Deserialize(CliJsonContext.Default.InterfaceDocument)
            ?? throw new JsonException($"{path} holds no document.");
    }
}

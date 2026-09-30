using System.CommandLine;
using System.Text.Json;
using WgAgent.Cli.Output;
using WgAgent.Core.Json;
using WgAgent.Core.Model;
using WgAgent.Service;

namespace WgAgent.Cli;

/// <summary><c>peer add | list | get | update | remove</c> — REQ-CLI-001.</summary>
internal static class PeerCommands
{
    public static Command Build(CliContext context, GlobalOptions global)
    {
        var command = new Command("peer", "Add, read, change and remove the peers of an interface.");
        command.Subcommands.Add(Add(context, global));
        command.Subcommands.Add(List(context, global));
        command.Subcommands.Add(Get(context, global));
        command.Subcommands.Add(Update(context, global));
        command.Subcommands.Add(Remove(context, global));
        return command;
    }

    private static Argument<string> InterfaceName() => new("interface") { Description = "The interface's name." };
    private static Argument<string> PublicKey() => new("public-key") { Description = "The peer's public key, in standard base64." };

    private static Command Add(CliContext context, GlobalOptions global)
    {
        var interfaceName = InterfaceName();
        var fields = new PeerFields();
        var publicKey = Flags.Scalar("public_key", "The peer's public key, in standard base64: the peer keeps its private key.");
        var generateKeypair = Flags.Switch("generate_keypair", "Generate the peer's key pair and print its client configuration.");
        var generatePresharedKey = Flags.Switch("generate_preshared_key", "Generate a preshared key for the peer.");
        var clientAllowedIps = Flags.List("client_allowed_ips", "An AllowedIPs entry of the client configuration; once per entry.");
        var clientKeepalive = Flags.Scalar("client_persistent_keepalive", "The client configuration's keepalive, in seconds; 0 omits it.");
        var dns = Flags.List("dns", "A DNS server of the client configuration; once per server.");
        var command = new Command("add", "Add a peer: a public key it keeps, or a key pair the agent generates.")
        {
            interfaceName, publicKey, generateKeypair, generatePresharedKey, clientAllowedIps, clientKeepalive, dns,
        };
        fields.AddTo(command);
        Cli.Act(command, context, global, run =>
        {
            var clientAllowed = Flags.Entries(run.Parse, clientAllowedIps);
            var result = run.Service.CreatePeer(run.Parse.GetValue(interfaceName)!, new CreatePeerRequest
            {
                PublicKey = Flags.Text(run.Parse, publicKey),
                GenerateKeypair = run.Parse.GetValue(generateKeypair),
                GeneratePresharedKey = run.Parse.GetValue(generatePresharedKey),
                Spec = fields.Apply(run.Parse, new PeerSpec()),
                ClientAllowedIps = clientAllowed.Count > 0 ? clientAllowed : null,
                ClientPersistentKeepalive = Flags.Number(run.Parse, clientKeepalive),
                Dns = Flags.Entries(run.Parse, dns),
            });
            Write(run, result);
        });
        return command;
    }

    private static Command List(CliContext context, GlobalOptions global)
    {
        var interfaceName = InterfaceName();
        var command = new Command("list", "List the peers of an interface.") { interfaceName };
        Cli.Act(command, context, global, run =>
        {
            var peers = run.Service.ListPeers(run.Parse.GetValue(interfaceName)!);
            if (run.Json) run.Out.WriteLine(JsonSerializer.Serialize(peers, CoreJsonContext.Default.IReadOnlyListPeerResource));
            else TextOutput.Peers(run.Out, peers);
        });
        return command;
    }

    private static Command Get(CliContext context, GlobalOptions global)
    {
        var interfaceName = InterfaceName();
        var publicKey = PublicKey();
        var command = new Command("get", "Show a peer, its spec and its status.") { interfaceName, publicKey };
        Cli.Act(command, context, global, run =>
        {
            var peer = run.Service.GetPeer(run.Parse.GetValue(interfaceName)!, run.Parse.GetValue(publicKey)!);
            if (run.Json) run.Out.WriteLine(JsonSerializer.Serialize(peer, CoreJsonContext.Default.PeerResource));
            else TextOutput.Peer(run.Out, peer);
        });
        return command;
    }

    private static Command Update(CliContext context, GlobalOptions global)
    {
        var interfaceName = InterfaceName();
        var publicKey = PublicKey();
        var fields = new PeerFields();
        var command = new Command("update", "Change the fields of a peer the flags name.") { interfaceName, publicKey };
        fields.AddTo(command);
        Cli.Act(command, context, global, run =>
        {
            var result = run.Service.UpdatePeer(run.Parse.GetValue(interfaceName)!, run.Parse.GetValue(publicKey)!,
                stored => fields.Apply(run.Parse, stored));   // REQ-CLI-027
            if (run.Json)
            {
                var outcome = new PeerOutcome { Peer = result.Resource, Restarted = result.Restarted };
                run.Out.WriteLine(JsonSerializer.Serialize(outcome, CliJsonContext.Default.PeerOutcome));
                return;
            }
            TextOutput.Peer(run.Out, result.Resource);
            run.Restarted(result.Resource.InterfaceName, result.Restarted);
        });
        return command;
    }

    private static Command Remove(CliContext context, GlobalOptions global)
    {
        var interfaceName = InterfaceName();
        var publicKey = PublicKey();
        var command = new Command("remove", "Remove a peer from an interface.") { interfaceName, publicKey };
        Cli.Act(command, context, global, run =>
            run.Service.DeletePeer(run.Parse.GetValue(interfaceName)!, run.Parse.GetValue(publicKey)!));
        return command;
    }

    private static void Write(Invocation run, CreatePeerResult result)
    {
        if (run.Json)
        {
            var outcome = new PeerOutcome
            {
                Peer = result.Peer,
                Restarted = result.Restarted,
                PrivateKey = result.GeneratedPrivateKey?.Reveal(),        // REQ-KEY-011
                PresharedKey = result.GeneratedPresharedKey?.Reveal(),    // REQ-KEY-021
                ClientConfiguration = result.ClientConfiguration,         // REQ-KEY-042
            };
            run.Out.WriteLine(JsonSerializer.Serialize(outcome, CliJsonContext.Default.PeerOutcome));
            return;
        }

        if (result.ClientConfiguration is { } configuration)
        {
            // REQ-CLI-032: the file alone on standard output, everything else on standard error.
            run.Out.Write(configuration);
            run.Error.WriteLine($"wg-agent: added the peer {result.Peer.PublicKey} to {result.Peer.InterfaceName}.");
            foreach (var warning in result.Peer.Status.Warnings) run.Error.WriteLine($"warning: {warning.Code}: {warning.Message}");
        }
        else
        {
            TextOutput.Peer(run.Out, result.Peer);
            if (result.GeneratedPresharedKey is { } psk) run.Out.WriteLine($"{"preshared key:",-19}{psk.Reveal()}");   // REQ-KEY-021
        }
        run.Restarted(result.Peer.InterfaceName, result.Restarted);
    }
}

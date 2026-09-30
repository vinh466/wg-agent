using System.Text;
using WgAgent.Platform;

namespace WgAgent.Core.Render;

/// <summary>What goes into a client configuration — SPEC-06 section 4.</summary>
public sealed record ClientConfig
{
    public required SecretKey PrivateKey { get; init; }
    public required IReadOnlyList<string> Addresses { get; init; }
    public IReadOnlyList<string> Dns { get; init; } = [];
    public required string ServerPublicKey { get; init; }
    public SecretKey? PresharedKey { get; init; }
    public required IReadOnlyList<string> AllowedIps { get; init; }
    public required string Endpoint { get; init; }
    public uint PersistentKeepalive { get; init; }
}

/// <summary>Renders a client configuration in the wg-quick format any WireGuard client reads (REQ-KEY-035).</summary>
public static class ClientConfigRenderer
{
    public const uint DefaultKeepalive = 25;

    public static string Render(ClientConfig config)
    {
        var text = new StringBuilder();
        text.Append("[Interface]\n");
        text.Append("PrivateKey = ").Append(config.PrivateKey.Reveal()).Append('\n');
        text.Append("Address = ").Append(string.Join(", ", config.Addresses)).Append('\n');
        if (config.Dns.Count > 0) text.Append("DNS = ").Append(string.Join(", ", config.Dns)).Append('\n');
        text.Append('\n');
        text.Append("[Peer]\n");
        text.Append("PublicKey = ").Append(config.ServerPublicKey).Append('\n');
        if (config.PresharedKey is { } psk) text.Append("PresharedKey = ").Append(psk.Reveal()).Append('\n');
        text.Append("AllowedIPs = ").Append(string.Join(", ", config.AllowedIps)).Append('\n');
        text.Append("Endpoint = ").Append(config.Endpoint).Append('\n');
        if (config.PersistentKeepalive > 0) text.Append("PersistentKeepalive = ").Append(config.PersistentKeepalive).Append('\n');
        return text.ToString();
    }
}

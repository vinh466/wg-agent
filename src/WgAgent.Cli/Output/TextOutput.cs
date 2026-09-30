using System.Globalization;
using WgAgent.Core.Model;

namespace WgAgent.Cli.Output;

/// <summary>
/// The human-readable form of REQ-CLI-020. It prints what the service returns, which carries no
/// private key and no stored preshared key (REQ-RES-013, REQ-RES-022), so nothing here can leak one
/// (REQ-CLI-022).
/// </summary>
public static class TextOutput
{
    public static void Interface(TextWriter output, InterfaceResource resource)
    {
        var spec = resource.Spec!;
        var status = resource.Status;
        Row(output, "name", resource.Name);
        Row(output, "public key", status.PublicKey);
        Row(output, "state", status.OperState.ToString().ToUpperInvariant());
        Row(output, "listen port", (status.ListenPort ?? spec.ListenPort)?.ToString(CultureInfo.InvariantCulture));
        Row(output, "addresses", string.Join(", ", spec.Addresses ?? []));
        Row(output, "mtu", spec.Mtu?.ToString(CultureInfo.InvariantCulture));
        Row(output, "enabled", spec.Enabled is false ? "false" : "true");
        Row(output, "peers", status.PeerCount?.ToString(CultureInfo.InvariantCulture));
        Row(output, "created", status.CreatedAt?.ToString("u", CultureInfo.InvariantCulture));
        Row(output, "labels", Labels(spec.Labels));
        foreach (var command in spec.PostUp ?? []) Row(output, "post up", command);
        foreach (var command in spec.PostDown ?? []) Row(output, "post down", command);
        Warnings(output, status.Warnings);
    }

    public static void Interfaces(TextWriter output, IReadOnlyList<InterfaceResource> resources)
    {
        Table(output, ["NAME", "STATE", "PORT", "PEERS", "ADDRESSES"], resources.Select(r => new[]
        {
            r.Name,
            r.Status.OperState.ToString().ToUpperInvariant(),
            (r.Status.ListenPort ?? r.Spec?.ListenPort)?.ToString(CultureInfo.InvariantCulture) ?? "-",
            r.Status.PeerCount?.ToString(CultureInfo.InvariantCulture) ?? "-",
            string.Join(", ", r.Spec?.Addresses ?? []),
        }));
    }

    public static void Peer(TextWriter output, PeerResource resource)
    {
        var status = resource.Status;
        Row(output, "interface", resource.InterfaceName);
        Row(output, "public key", resource.PublicKey);
        Row(output, "allowed ips", string.Join(", ", resource.Spec.AllowedIps ?? []));
        Row(output, "endpoint", resource.Spec.Endpoint);
        Row(output, "keepalive", resource.Spec.PersistentKeepalive?.ToString(CultureInfo.InvariantCulture));
        Row(output, "labels", Labels(resource.Spec.Labels));
        Row(output, "online", status.Online ? "yes" : "no");
        Row(output, "handshake", Handshake(status));
        Row(output, "resolved endpoint", status.ResolvedEndpoint);
        Row(output, "received", status.RxBytes?.ToString(CultureInfo.InvariantCulture));
        Row(output, "sent", status.TxBytes?.ToString(CultureInfo.InvariantCulture));
        Warnings(output, status.Warnings);
    }

    public static void Peers(TextWriter output, IReadOnlyList<PeerResource> resources)
    {
        Table(output, ["PUBLIC KEY", "ALLOWED IPS", "ENDPOINT", "HANDSHAKE", "ONLINE"], resources.Select(r => new[]
        {
            r.PublicKey,
            string.Join(", ", r.Spec.AllowedIps ?? []),
            r.Status.ResolvedEndpoint ?? r.Spec.Endpoint ?? "-",
            Handshake(r.Status),
            r.Status.Online ? "yes" : "no",
        }));
    }

    private static string Handshake(PeerStatus status) =>
        status.HandshakeAgeSeconds is { } age ? $"{age} s ago" : status.RxBytes is null ? "-" : "never";

    private static string? Labels(IReadOnlyDictionary<string, string>? labels) =>
        labels is { Count: > 0 } ? string.Join(", ", labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={l.Value}")) : null;

    private static void Row(TextWriter output, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value)) output.WriteLine($"{label + ":",-19}{value}");
    }

    private static void Warnings(TextWriter output, IReadOnlyList<Warning> warnings)
    {
        foreach (var warning in warnings) output.WriteLine($"warning: {warning.Code}: {warning.Message}");
    }

    private static void Table(TextWriter output, string[] header, IEnumerable<string[]> rows)
    {
        var all = rows.Prepend(header).ToList();
        var widths = header.Select((_, i) => all.Max(r => r[i].Length)).ToArray();
        foreach (var row in all)
            output.WriteLine(string.Join("  ", row.Select((cell, i) => i == row.Length - 1 ? cell : cell.PadRight(widths[i]))).TrimEnd());
    }
}

using System.Text;
using WgAgent.Core.Model;

namespace WgAgent.Core.Render;

/// <summary>
/// Renders an interface's file. Only the keys of REQ-APL-003 are ever written; peers come in the
/// order of their public keys, so the same state always renders the same text.
/// </summary>
public static class ConfigRenderer
{
    /// <summary>The whole file, for /etc/wireguard/&lt;name&gt;.conf.</summary>
    public static string File(InterfaceSpec spec, IReadOnlyDictionary<string, PeerSpec> peers) => Render(spec, peers, wgQuickKeys: true);

    /// <summary>The same, without the keys only wg-quick understands, for <c>wg syncconf</c> (REQ-APL-005).</summary>
    public static string WireGuardOnly(InterfaceSpec spec, IReadOnlyDictionary<string, PeerSpec> peers) => Render(spec, peers, wgQuickKeys: false);

    private static string Render(InterfaceSpec spec, IReadOnlyDictionary<string, PeerSpec> peers, bool wgQuickKeys)
    {
        var privateKey = spec.PrivateKey ?? throw new InvalidOperationException("An interface is rendered only once it has a private key.");
        var text = new StringBuilder();
        text.Append("[Interface]\n");
        text.Append("PrivateKey = ").Append(privateKey.Reveal()).Append('\n');
        text.Append("ListenPort = ").Append(spec.ListenPort ?? Defaults.ListenPort).Append('\n');
        if (wgQuickKeys)
        {
            text.Append("Address = ").Append(string.Join(", ", spec.Addresses ?? [])).Append('\n');
            text.Append("MTU = ").Append(spec.Mtu ?? Defaults.Mtu).Append('\n');
            foreach (var command in spec.PostUp ?? []) text.Append("PostUp = ").Append(command).Append('\n');
            foreach (var command in spec.PostDown ?? []) text.Append("PostDown = ").Append(command).Append('\n');
        }
        foreach (var (publicKey, peer) in peers.OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append('\n').Append(PeerSection(publicKey, peer));
        return text.ToString();
    }

    /// <summary>One [Peer] section; also what two states compare to tell whether a peer changed.</summary>
    public static string PeerSection(string publicKey, PeerSpec peer)
    {
        var text = new StringBuilder();
        text.Append("[Peer]\n");
        text.Append("PublicKey = ").Append(publicKey).Append('\n');
        if (peer.PresharedKey is { } psk) text.Append("PresharedKey = ").Append(psk.Reveal()).Append('\n');
        text.Append("AllowedIPs = ").Append(string.Join(", ", peer.AllowedIps ?? [])).Append('\n');
        if (peer.Endpoint is { } endpoint) text.Append("Endpoint = ").Append(endpoint).Append('\n');
        if (peer.PersistentKeepalive is > 0) text.Append("PersistentKeepalive = ").Append(peer.PersistentKeepalive).Append('\n');
        return text.ToString();
    }
}

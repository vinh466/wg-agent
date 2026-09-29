using WgAgent.Platform;

namespace WgAgent.Testing;

// The write side of IDevice. It mutates the in-memory node, so a test can drive
// a whole reconcile pass and then assert what the node became — the property
// that keeps the reconcile algorithm testable without privilege.
public sealed partial class FakeNode
{
    public void Configure(string name, DeviceConfig config)
    {
        if (!Devices.TryGetValue(name, out var d))
            throw new InvalidOperationException($"no such device \"{name}\"");
        Calls.Add($"Configure({name})");

        if (config.PrivateKey is { } priv)
        {
            // The kernel derives the public key on receipt, so the fake does
            // too. Leaving it empty would let a test above the port see a device
            // the kernel could not be in: a private key and no public one.
            var pub = priv.IsPresent ? priv.GetPublicKey() : Key.None;
            d = d with { PrivateKey = priv, PublicKey = pub };
            Calls.Add($"SetPrivateKey({name})");
        }
        if (config.ListenPort is { } port)
        {
            d = d with { ListenPort = port };
            Calls.Add($"SetListenPort({name},{port})");
        }
        if (config.Fwmark is { } fwmark)
        {
            d = d with { Fwmark = fwmark };
            Calls.Add($"SetFwmark({name},{fwmark:x})");
        }

        var peers = new List<PeerState>(d.Peers);

        if (config.ReplacePeers)
        {
            peers.Clear();
            Calls.Add($"ReplacePeers({name})");
        }

        foreach (var pc in config.Peers)
        {
            int idx = peers.FindIndex(p => p.PublicKey == pc.PublicKey);

            if (pc.Remove)
            {
                if (idx >= 0) peers.RemoveAt(idx);
                Calls.Add($"RemovePeer({name},{Short(pc.PublicKey)})");
                continue;
            }
            if (idx < 0 && pc.UpdateOnly) continue; // the kernel would not create it either.
            if (idx < 0)
            {
                peers.Add(new PeerState { PublicKey = pc.PublicKey });
                idx = peers.Count - 1;
                Calls.Add($"AddPeer({name},{Short(pc.PublicKey)})");
            }
            else
            {
                Calls.Add($"UpdatePeer({name},{Short(pc.PublicKey)})");
            }

            var p = peers[idx];
            if (pc.PresharedKey is { } psk) p = p with { PresharedKey = psk };
            if (pc.ReplaceAllowedIPs || (pc.AllowedIPs is { Count: > 0 }))
                p = p with { AllowedIPs = pc.AllowedIPs is null ? [] : [.. pc.AllowedIPs] };
            if (pc.Endpoint is { } endpoint)
            {
                p = p with { Endpoint = endpoint };
                Calls.Add($"SetEndpoint({name},{Short(pc.PublicKey)})");
            }
            if (pc.PersistentKeepalive is { } keepalive) p = p with { PersistentKeepalive = keepalive };
            peers[idx] = p;
        }

        Devices[name] = d with { Peers = peers };
    }
}

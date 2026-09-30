namespace WgAgent.Core.Model;

/// <summary>The defaults of SPEC-01 sections 3.2 and 4.2, applied under REQ-API-075.</summary>
public static class Defaults
{
    public const uint ListenPort = 51820;
    public const uint Mtu = 1420;
    public const bool Enabled = true;
    public const uint PersistentKeepalive = 0;

    /// <summary>Fills every omitted field but the private key, which only generation supplies.</summary>
    public static InterfaceSpec Apply(InterfaceSpec spec) => spec with
    {
        ListenPort = spec.ListenPort ?? ListenPort,
        Addresses = spec.Addresses ?? [],
        Mtu = spec.Mtu ?? Mtu,
        Enabled = spec.Enabled ?? Enabled,
        Labels = spec.Labels ?? new Dictionary<string, string>(),
        PostUp = spec.PostUp ?? [],
        PostDown = spec.PostDown ?? [],
    };

    public static PeerSpec Apply(PeerSpec spec) => spec with
    {
        AllowedIps = spec.AllowedIps ?? [],
        PersistentKeepalive = spec.PersistentKeepalive ?? PersistentKeepalive,
        Labels = spec.Labels ?? new Dictionary<string, string>(),
    };
}

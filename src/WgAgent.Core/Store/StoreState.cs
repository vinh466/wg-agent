using WgAgent.Core.Model;

namespace WgAgent.Core.Store;

/// <summary>
/// Desired state as the store holds it: spec values and the agent's own records, nothing the
/// kernel reports (REQ-RCN-002, REQ-RCN-050). Peers sit under their interface, so deleting an
/// interface deletes its peers in the same write (REQ-RCN-038).
/// </summary>
public sealed record StoreState(IReadOnlyDictionary<string, StoredInterface> Interfaces)
{
    public static StoreState Empty { get; } = new(new Dictionary<string, StoredInterface>());

    public StoreState With(string name, StoredInterface stored)
    {
        var next = new Dictionary<string, StoredInterface>(Interfaces) { [name] = stored };
        return new StoreState(next);
    }

    public StoreState Without(string name)
    {
        var next = new Dictionary<string, StoredInterface>(Interfaces);
        next.Remove(name);
        return new StoreState(next);
    }
}

/// <summary>One interface: its resolved spec, when the agent created it, and its peers by public key.</summary>
public sealed record StoredInterface(InterfaceSpec Spec, DateTimeOffset CreatedAt, IReadOnlyDictionary<string, PeerSpec> Peers)
{
    public StoredInterface WithPeer(string publicKey, PeerSpec spec) =>
        this with { Peers = new Dictionary<string, PeerSpec>(Peers) { [publicKey] = spec } };

    public StoredInterface WithoutPeer(string publicKey)
    {
        var next = new Dictionary<string, PeerSpec>(Peers);
        next.Remove(publicKey);
        return this with { Peers = next };
    }
}

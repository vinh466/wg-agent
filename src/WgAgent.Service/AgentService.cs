using WgAgent.Core;
using WgAgent.Core.Apply;
using WgAgent.Core.Model;
using WgAgent.Core.Render;
using WgAgent.Core.Status;
using WgAgent.Core.Store;
using WgAgent.Core.Validation;
using WgAgent.Platform;

namespace WgAgent.Service;

/// <summary>
/// The operations of SPEC-04 section 3, shared by the CLI and the API (REQ-CLI-023). A write takes
/// the one lock (REQ-RCN-042), then validates, applies and stores, in that order (REQ-API-020).
/// </summary>
public sealed class AgentService
{
    private readonly StateStore _store;
    private readonly HostPorts _host;
    private readonly AgentOptions _options;
    private readonly TimeProvider _clock;
    private readonly Applier _applier;
    private readonly StatusReader _status;

    public AgentService(StateStore store, HostPorts host, AgentOptions options, TimeProvider clock)
    {
        _store = store;
        _host = host;
        _options = options;
        _clock = clock;
        _applier = new Applier(host.Wg, host.Units, host.Files, host.Deadline);
        _status = new StatusReader(host.Wg, host.Units, clock, options.PeerOnlineThreshold);
    }

    // ---- Reads

    public IReadOnlyList<InterfaceResource> ListInterfaces()
    {
        var state = _store.Load();
        return [.. state.Interfaces.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => Describe(i.Key, i.Value))];
    }

    public InterfaceResource GetInterface(string name) => Describe(name, Managed(_store.Load(), name));

    public IReadOnlyList<PeerResource> ListPeers(string interfaceName)
    {
        var stored = Managed(_store.Load(), interfaceName);
        var seen = _status.Observe(interfaceName);
        return [.. stored.Peers.Keys.Order(StringComparer.Ordinal).Select(key => DescribePeer(interfaceName, stored, key, seen))];
    }

    public PeerResource GetPeer(string interfaceName, string publicKey)
    {
        var stored = Managed(_store.Load(), interfaceName);
        if (!stored.Peers.ContainsKey(publicKey)) throw PeerNotFound(interfaceName, publicKey);
        return DescribePeer(interfaceName, stored, publicKey, _status.Observe(interfaceName));
    }

    // ---- Interface writes

    /// <summary>CreateInterface, or with <paramref name="peers"/> the one write of REQ-CLI-025.</summary>
    public WriteResult<InterfaceResource> CreateInterface(string name, InterfaceSpec spec,
        IReadOnlyList<(string PublicKey, PeerSpec Spec)>? peers = null)
    {
        using var held = Lock();
        var state = _store.Load();

        var resolved = Defaults.Apply(spec);
        Throw(InterfaceRules.Check(name, resolved, creating: true, ObserveHost(), Others(state, name)));
        resolved = resolved with { PrivateKey = resolved.PrivateKey ?? _host.Wg.GenerateKey() };   // REQ-KEY-001

        var peerSpecs = new Dictionary<string, PeerSpec>();
        if (peers is { Count: > 0 })
        {
            var interfaceKey = _host.Wg.PublicKeyOf(resolved.PrivateKey);
            foreach (var (publicKey, peerSpec) in peers)
            {
                if (peerSpecs.ContainsKey(publicKey))
                    throw new AgentException(ReasonCodes.PeerExists, $"The document lists the peer {publicKey} twice.");
                var peer = Defaults.Apply(peerSpec);
                Throw(PeerRules.Check(new PeerCheck
                {
                    Interface = resolved, InterfacePublicKey = interfaceKey, PublicKey = publicKey, Spec = peer,
                    OtherPeers = [.. peerSpecs.Values],
                }));
                peerSpecs[publicKey] = peer;
            }
        }

        var after = new StoredInterface(resolved, _clock.GetUtcNow(), peerSpecs);
        var result = Commit(state, name, null, after);
        return new WriteResult<InterfaceResource>(Describe(name, after), result.Restarted);
    }

    /// <param name="keepHooks">True for a write from the API, which leaves the CLI's hooks alone (REQ-API-085).</param>
    public WriteResult<InterfaceResource> UpdateInterface(string name, InterfaceSpec spec, bool keepHooks = false) =>
        UpdateInterface(name, _ => spec, keepHooks);

    /// <summary>
    /// UpdateInterface with the spec computed from the stored one under the same lock, for the CLI,
    /// which changes only the fields its flags name (REQ-CLI-027).
    /// </summary>
    public WriteResult<InterfaceResource> UpdateInterface(string name, Func<InterfaceSpec, InterfaceSpec> change, bool keepHooks = false)
    {
        using var held = Lock();
        var state = _store.Load();
        var before = Managed(state, name);
        var spec = change(before.Spec);

        // REQ-API-064 replaces the whole spec; REQ-API-065 keeps the write-only key it omits.
        var resolved = Defaults.Apply(spec) with { PrivateKey = spec.PrivateKey ?? before.Spec.PrivateKey };
        if (keepHooks) resolved = resolved with { PostUp = before.Spec.PostUp, PostDown = before.Spec.PostDown };
        Throw(InterfaceRules.Check(name, resolved, creating: false, ObserveHost(), Others(state, name)));

        var after = before with { Spec = resolved };
        var result = Commit(state, name, before, after);
        return new WriteResult<InterfaceResource>(Describe(name, after), result.Restarted);
    }

    public void DeleteInterface(string name)
    {
        using var held = Lock();
        var state = _store.Load();
        Commit(state, name, Managed(state, name), null);   // REQ-RCN-032, REQ-RCN-038
    }

    // ---- Peer writes

    public CreatePeerResult CreatePeer(string interfaceName, CreatePeerRequest request)
    {
        using var held = Lock();
        var state = _store.Load();
        var before = Managed(state, interfaceName);

        if (!request.GenerateKeypair && request.PublicKey is not null && before.Peers.ContainsKey(request.PublicKey))
            throw new AgentException(ReasonCodes.PeerExists, $"The interface '{interfaceName}' already has the peer {request.PublicKey}.");   // REQ-API-071

        var interfaceKey = _host.Wg.PublicKeyOf(before.Spec.PrivateKey!);
        var spec = Defaults.Apply(request.Spec);
        Throw(PeerRules.Check(new PeerCheck
        {
            Interface = before.Spec,
            InterfacePublicKey = interfaceKey,
            PublicKey = request.PublicKey,
            Spec = spec,
            GenerateKeypair = request.GenerateKeypair,
            GeneratePresharedKey = request.GeneratePresharedKey,
            OtherPeers = [.. before.Peers.Values],
            ClientAllowedIps = request.ClientAllowedIps,
            ClientPersistentKeepalive = request.ClientPersistentKeepalive,
            Dns = request.Dns,
            NodeEndpoint = request.NodeEndpoint,
        }));

        var host = request.NodeEndpoint ?? _options.NodeEndpoint;
        if (request.GenerateKeypair && host is null)
            throw new AgentException(ReasonCodes.EndpointRequired,
                "A client configuration needs the host clients reach this node at: give node_endpoint, or set node.endpoint.");   // REQ-KEY-038

        SecretKey? privateKey = null;
        var publicKey = request.PublicKey!;
        if (request.GenerateKeypair)
        {
            privateKey = _host.Wg.GenerateKey();
            publicKey = _host.Wg.PublicKeyOf(privateKey).ToString();
        }
        var generatedPsk = request.GeneratePresharedKey ? _host.Wg.GeneratePresharedKey() : null;   // REQ-KEY-021
        if (generatedPsk is not null) spec = spec with { PresharedKey = generatedPsk };

        var subnets = Networks.SubnetsOf(before.Spec.Addresses ?? []);
        if (request.GenerateKeypair && (spec.AllowedIps ?? []).Count == 0)
        {
            var interfaceAddresses = (before.Spec.Addresses ?? []).Select(a => Cidr.TryParse(a, out var c) ? c : (Cidr?)null).OfType<Cidr>();
            var taken = before.Peers.Values.SelectMany(p => p.AllowedIps ?? []).Select(a => Cidr.TryParse(a, out var c) ? c : (Cidr?)null).OfType<Cidr>();
            spec = spec with { AllowedIps = [Networks.LowestFreeHost(subnets[0], interfaceAddresses, taken)!.Value.ToString()] };   // REQ-KEY-047
        }

        var after = before.WithPeer(publicKey, spec);
        var result = Commit(state, interfaceName, before, after);

        string? clientConfiguration = null;
        if (privateKey is not null)
        {
            clientConfiguration = ClientConfigRenderer.Render(new ClientConfig
            {
                PrivateKey = privateKey,
                Addresses = [.. (spec.AllowedIps ?? []).Where(a => Cidr.TryParse(a, out var c) && Networks.IsWithin(c, subnets))],   // REQ-KEY-043
                Dns = request.Dns ?? [],   // REQ-KEY-034
                ServerPublicKey = interfaceKey.ToString(),
                PresharedKey = generatedPsk,   // REQ-KEY-045
                AllowedIps = request.ClientAllowedIps is { Count: > 0 } chosen ? chosen : [.. subnets.Select(s => s.ToString())],   // REQ-KEY-044
                Endpoint = $"{host}:{after.Spec.ListenPort ?? Defaults.ListenPort}",   // REQ-KEY-036, REQ-KEY-037
                PersistentKeepalive = request.ClientPersistentKeepalive ?? ClientConfigRenderer.DefaultKeepalive,   // REQ-KEY-046
            });
        }
        var peer = DescribePeer(interfaceName, after, publicKey, _status.Observe(interfaceName));
        return new CreatePeerResult(peer, result.Restarted, privateKey, generatedPsk, clientConfiguration);
    }

    public WriteResult<PeerResource> UpdatePeer(string interfaceName, string publicKey, PeerSpec spec) =>
        UpdatePeer(interfaceName, publicKey, _ => spec);

    /// <summary>UpdatePeer with the spec computed from the stored one under the same lock (REQ-CLI-027).</summary>
    public WriteResult<PeerResource> UpdatePeer(string interfaceName, string publicKey, Func<PeerSpec, PeerSpec> change)
    {
        using var held = Lock();
        var state = _store.Load();
        var before = Managed(state, interfaceName);
        if (!before.Peers.TryGetValue(publicKey, out var old)) throw PeerNotFound(interfaceName, publicKey);
        var spec = change(old);

        var resolved = Defaults.Apply(spec) with { PresharedKey = spec.PresharedKey ?? old.PresharedKey };   // REQ-API-065
        Throw(PeerRules.Check(new PeerCheck
        {
            Interface = before.Spec,
            InterfacePublicKey = _host.Wg.PublicKeyOf(before.Spec.PrivateKey!),
            PublicKey = publicKey,
            Spec = resolved,
            OtherPeers = [.. before.Peers.Where(p => p.Key != publicKey).Select(p => p.Value)],
        }));

        var after = before.WithPeer(publicKey, resolved);
        var result = Commit(state, interfaceName, before, after);
        return new WriteResult<PeerResource>(DescribePeer(interfaceName, after, publicKey, _status.Observe(interfaceName)), result.Restarted);
    }

    public void DeletePeer(string interfaceName, string publicKey)
    {
        using var held = Lock();
        var state = _store.Load();
        var before = Managed(state, interfaceName);
        if (!before.Peers.ContainsKey(publicKey)) throw PeerNotFound(interfaceName, publicKey);
        Commit(state, interfaceName, before, before.WithoutPeer(publicKey));
    }

    // ---- Shared steps

    /// <summary>
    /// Applies, then stores (REQ-API-020). A store that cannot be written has the applied change
    /// undone, so the interface never runs a configuration the store does not describe (REQ-APL-011).
    /// </summary>
    private ApplyResult Commit(StoreState state, string name, StoredInterface? before, StoredInterface? after)
    {
        var result = Within(() => _applier.Apply(name, before, after));
        try
        {
            _store.Save(after is null ? state.Without(name) : state.With(name, after));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            string? restoreFailure = null;
            try { Within(() => _applier.Apply(name, after, before)); }
            catch (AgentException undo) { restoreFailure = undo.Message; }
            throw new AgentException(ReasonCodes.ApplyFailed, restoreFailure is null
                ? $"Storing the change to '{name}' failed, and the previous configuration was restored: {failure.Message}"
                : $"Storing the change to '{name}' failed: {failure.Message}. Restoring the previous configuration failed too: {restoreFailure}");
        }
        return result;
    }

    private T Within<T>(Func<T> step)
    {
        _host.Deadline.Begin(_options.ApplyTimeout);   // REQ-API-022
        try { return step(); }
        finally { _host.Deadline.End(); }
    }

    private StoreLock Lock() => StoreLock.Acquire(_store.LockPath, _options.ApplyTimeout);   // REQ-RCN-042, REQ-RCN-075

    private HostView ObserveHost()
    {
        var ports = _host.Wg.ListenPorts();
        return new HostView
        {
            Links = _host.Network.LinkNames(),
            ConfigFiles = _host.Files.Names(),
            WireGuardListenPorts = ports,
            WireGuardAddresses = ports.Keys.ToDictionary(n => n, n => _host.Network.AddressesOf(n)),
        };
    }

    private static Dictionary<string, InterfaceSpec> Others(StoreState state, string name) =>
        state.Interfaces.Where(i => i.Key != name).ToDictionary(i => i.Key, i => i.Value.Spec);

    private static StoredInterface Managed(StoreState state, string name) =>
        state.Interfaces.TryGetValue(name, out var stored) ? stored
            : throw new AgentException(ReasonCodes.InterfaceNotManaged, $"The agent manages no interface named '{name}'.");   // REQ-API-069

    private static AgentException PeerNotFound(string interfaceName, string publicKey) =>
        new(ReasonCodes.PeerNotFound, $"The interface '{interfaceName}' has no peer {publicKey}.");   // REQ-API-070

    private static void Throw(ValidationResult result)
    {
        if (result.Error is { } error) throw new AgentException(error.Code, error.Message);   // REQ-VAL-001
    }

    private InterfaceResource Describe(string name, StoredInterface stored)
    {
        var seen = _status.Observe(name);
        var publicKey = seen.Device?.PublicKey ?? _host.Wg.PublicKeyOf(stored.Spec.PrivateKey!);
        var warnings = InterfaceRules.Warnings(stored.Spec).Select(w => new Warning(w.Code, w.Message)).ToList();   // REQ-VAL-002
        return new InterfaceResource
        {
            Name = name,
            Spec = stored.Spec with { PrivateKey = null },   // REQ-RES-013
            Status = StatusReader.Interface(seen, stored, publicKey.ToString(), warnings),
        };
    }

    private PeerResource DescribePeer(string interfaceName, StoredInterface stored, string publicKey, StatusReader.Observation seen)
    {
        var spec = stored.Peers[publicKey];
        var others = stored.Peers.Where(p => p.Key != publicKey).Select(p => p.Value).ToList();
        var warnings = PeerRules.Warnings(stored.Spec, spec, others).Select(w => new Warning(w.Code, w.Message)).ToList();
        return new PeerResource
        {
            InterfaceName = interfaceName,
            PublicKey = publicKey,
            Spec = spec with { PresharedKey = null },   // REQ-RES-022
            Status = _status.Peer(seen, publicKey, warnings),
        };
    }
}

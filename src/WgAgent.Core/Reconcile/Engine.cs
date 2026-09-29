using WgAgent.Core.Model;
using WgAgent.Platform;

namespace WgAgent.Core.Reconcile;

/// <summary>
/// The desired-state access a pass needs. <see cref="Store.Store"/> satisfies
/// it.
/// </summary>
/// <remarks>
/// The engine reads through this rather than re-reading the file, because the
/// process running a pass is the one holding the lock of REQ-RCN-006.
/// </remarks>
public interface IReconcileStore
{
    IReadOnlyList<string> Names();
    bool TryGetInterface(string name, out InterfaceSpec spec);
    IReadOnlyList<Peer> Peers(string name);
    bool Describes(string name);
    bool DeletionRecord(string name);
    IReadOnlyList<string> DeletionNames();
    void ClearDeletion(string name);
}

/// <summary>The outcome of one pass, enough to drive the backoff of REQ-RCN-041.</summary>
public readonly record struct PassResult(int Failed, int Total)
{
    public bool Complete => Failed == 0;
}

/// <summary>Applies desired state to the kernel — the algorithm of REQ-RCN-022.</summary>
public sealed class Engine
{
    private readonly IReconcileStore _store;
    private readonly ILink _link;
    private readonly IDevice _device;

    // The forwarding sysctl of step 9. A null value skips that step, which lets
    // a test drive the rest of the algorithm on a host whose /proc/sys is
    // read-only.
    private readonly IHostFs? _hostFs;
    private readonly Func<DateTimeOffset> _now;

    private readonly object _mapLock = new();
    private readonly Dictionary<string, object> _locks = [];
    private readonly Dictionary<string, ReconcileStatus> _status = [];

    public Engine(IReconcileStore store, ILink link, IDevice device, IHostFs? hostFs = null, Func<DateTimeOffset>? now = null)
    {
        _store = store;
        _link = link;
        _device = device;
        _hostFs = hostFs;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    // The per-interface lock of REQ-RCN-042, created on first use. Every
    // operation on one interface serialises behind it, so an API write and a
    // periodic pass cannot interleave their reads and writes.
    private object LockFor(string name)
    {
        lock (_mapLock)
        {
            if (!_locks.TryGetValue(name, out var l))
            {
                l = new object();
                _locks[name] = l;
            }
            return l;
        }
    }

    private void PutStatus(ReconcileStatus s)
    {
        lock (_mapLock) _status[s.Name] = s;
    }

    /// <summary>The last status written for one interface.</summary>
    public bool TryGetStatus(string name, out ReconcileStatus status)
    {
        lock (_mapLock) return _status.TryGetValue(name, out status!);
    }

    /// <summary>Every status written, sorted by name.</summary>
    public IReadOnlyList<ReconcileStatus> Statuses()
    {
        lock (_mapLock)
            return [.. _status.Values.OrderBy(s => s.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Reconciles every interface desired state describes, then classifies the
    /// WireGuard links it does not — REQ-RCN-022 and REQ-RCN-036.
    /// </summary>
    /// <remarks>
    /// A failure on one interface does not stop the others: REQ-RCN-040 makes a
    /// failure a DEGRADED condition on that resource, and an interface unrelated
    /// to it is still owed a pass. The result reports how many failed, so a
    /// caller can drive the backoff of REQ-RCN-041.
    /// </remarks>
    public PassResult Pass()
    {
        var names = _store.Names();
        int failed = 0;
        foreach (string n in names)
        {
            if (Interface(n).Condition.State == ConditionState.Degraded) failed++;
        }
        Classify();
        return new PassResult(failed, names.Count);
    }

    /// <summary>
    /// Reconciles one interface and returns the status it wrote. It takes the
    /// per-interface lock of REQ-RCN-042, so it is the entry point for an API
    /// write as well as for a pass.
    /// </summary>
    public ReconcileStatus Interface(string name)
    {
        lock (LockFor(name))
        {
            var st = Apply(name);
            PutStatus(st);
            return st;
        }
    }

    // The algorithm of REQ-RCN-022, in the order the requirement fixes. Step 10
    // alone is deferred, under B-04.
    private ReconcileStatus Apply(string name)
    {
        var st = new ReconcileStatus
        {
            Name = name,
            Ownership = Ownership.Managed,
            OperState = OperState.Absent,
            UpdatedAt = _now(),
        };

        if (!_store.TryGetInterface(name, out var spec))
        {
            st.Ownership = OwnershipRule.Of(false, _store.DeletionRecord(name));
            st.Degraded(ReconcileReasons.ReconcileFailed, "desired state does not describe " + name);
            return st;
        }
        var peers = _store.Peers(name);

        // Step 1 — link present?
        bool present;
        try
        {
            present = LinkPresent(name);
        }
        catch (Exception e) when (e is not FormatException)
        {
            st.Degraded(ReconcileReasons.ReconcileFailed, e.Message);
            return st;
        }
        if (!present)
        {
            try { _link.Add(name); }
            catch (Exception e) { st.Degraded(ReconcileReasons.ReconcileFailed, e.Message); return st; }
        }

        LinkState ls;
        try { ls = _link.State(name); }
        catch (Exception e) { st.Degraded(ReconcileReasons.ReconcileFailed, e.Message); return st; }
        st.OperState = OperStateOf(ls);

        // Step 2 — link of type wireguard?
        if (ls.Type != "wireguard")
        {
            st.Degraded(ReconcileReasons.ReconcileFailed,
                $"{name} is a \"{ls.Type}\" link; the resource model describes wireguard links only");
            return st;
        }

        DeviceState ds;
        try { ds = _device.Snapshot(name); }
        catch (Exception e) { st.Degraded(ReconcileReasons.ReconcileFailed, e.Message); return st; }
        st.PeerCount = ds.Peers.Count;

        if (spec.PrivateKey.Length == 0)
        {
            // Leaving the kernel's key alone is the conservative reading:
            // clearing it would break every established client, and no
            // requirement asks for that. An absent value is a store that lost it.
            st.Warn(ReconcileReasons.StoreCorrupt,
                "desired state holds no private key for " + name + "; the kernel's is left in place");
        }

        // Steps 3 and 4 — device config and peer set, in one Configure call.
        Delta.DeviceDelta dd;
        List<PeerConfig> pcs;
        try
        {
            dd = Delta.Device(spec, ds);
            pcs = Delta.Peers(peers, ds);
        }
        catch (FormatException e)
        {
            st.Degraded(ReconcileReasons.StoreCorrupt, e.Message);
            return st;
        }
        if (dd.Changed || pcs.Count > 0)
        {
            var cfg = new DeviceConfig
            {
                PrivateKey = dd.PrivateKey,
                ListenPort = dd.ListenPort,
                Fwmark = dd.Fwmark,
                Peers = pcs,
            };
            try { _device.Configure(name, cfg); }
            catch (Exception e) { st.Degraded(ReconcileReasons.ReconcileFailed, e.Message); return st; }
            // peer_count is the count in the kernel, so it is read back rather
            // than assumed: a peer the kernel declined to create is not counted.
            try { st.PeerCount = _device.Snapshot(name).Peers.Count; }
            catch { /* the pass already wrote; a re-read failure is not fatal. */ }
        }

        // Step 5 — addresses.
        List<Cidr> addrAdd, addrDel;
        try { (addrAdd, addrDel) = Delta.Addresses(spec, ls); }
        catch (FormatException e) { st.Degraded(ReconcileReasons.StoreCorrupt, e.Message); return st; }
        try
        {
            foreach (var p in addrAdd) _link.AddAddress(name, p);
            foreach (var p in addrDel) _link.DeleteAddress(name, p);
        }
        catch (Exception e) { st.Degraded(ReconcileReasons.ReconcileFailed, e.Message); return st; }

        // Step 6 — MTU. A zero spec value leaves the kernel's default in place.
        if (spec.Mtu != 0 && spec.Mtu != ls.Mtu)
        {
            try { _link.SetMtu(name, spec.Mtu); }
            catch (Exception e) { st.Degraded(ReconcileReasons.ReconcileFailed, e.Message); return st; }
        }

        // Step 7 — enabled. This precedes routing because the kernel refuses a
        // route whose output device is down.
        if (spec.Enabled != ls.AdminUp)
        {
            try
            {
                if (spec.Enabled) _link.SetUp(name); else _link.SetDown(name);
            }
            catch (Exception e) { st.Degraded(ReconcileReasons.ReconcileFailed, e.Message); return st; }
            st.OperState = spec.Enabled ? OperState.Up : OperState.Down;
        }

        // Step 8 — routes. REQ-RCN-024 skips this while the link is down.
        if (spec.ManageRoutes && spec.Enabled)
        {
            try { SyncRoutes(name, peers); }
            catch (FormatException e) { st.Degraded(ReconcileReasons.StoreCorrupt, e.Message); return st; }
            catch (Exception e) { st.Degraded(ReconcileReasons.ReconcileFailed, e.Message); return st; }
        }

        // Step 9 — the forwarding sysctl. REQ-FWD-020 is one of the three
        // SPEC-02 requirements B-04 keeps, because without it traffic between
        // two peers of one interface is not forwarded at all. REQ-FWD-022 is
        // satisfied structurally: this runs for an interface desired state
        // describes, the membership test the requirement names.
        if (_hostFs is not null && NeedsForwarding(spec.ForwardPolicy))
        {
            try { _hostFs.SetForwardingSysctl(name, "1"); }
            catch (Exception e) { st.Degraded(ReconcileReasons.SysctlWriteDenied, e.Message); return st; }
        }

        // Step 10 — nftables — is deferred under B-04, so a DENY axis is stored
        // and not enforced.

        // Step 11 — status.
        st.Ready();
        return st;
    }

    // The condition of REQ-FWD-020. The requirement reads inter_interface != DENY,
    // which an unset axis would satisfy; an unset axis is an invalid spec rather
    // than a permissive one, so the test is for a value that explicitly permits
    // forwarding — the direction that does not open forwarding by accident.
    private static bool NeedsForwarding(ForwardPolicySpec fp)
        => fp.IntraInterface == Axis.Allow
        || fp.InterInterface is Axis.Allow or Axis.AllowList;

    private void SyncRoutes(string name, IReadOnlyList<Peer> peers)
    {
        var (add, del) = Delta.Routes(peers, _link.Routes(name));
        foreach (var p in add) _link.AddRoute(name, p);
        foreach (var p in del) _link.DeleteRoute(name, p);
    }

    // REQ-RCN-036: every WireGuard link absent from desired state is FOREIGN or
    // ORPHANED. Neither is touched — REQ-RCN-030 leaves a foreign link alone and
    // REQ-RCN-035 forbids acting on an orphan. It also clears the deletion
    // records of REQ-RCN-037, whose link is gone.
    private void Classify()
    {
        var devices = _device.Names();
        var live = new HashSet<string>(devices);

        foreach (string n in devices.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (_store.Describes(n)) continue;
            var st = new ReconcileStatus
            {
                Name = n,
                Ownership = OwnershipRule.Of(false, _store.DeletionRecord(n)),
                OperState = OperState.Absent,
                UpdatedAt = _now(),
            };
            try { st.OperState = OperStateOf(_link.State(n)); } catch { /* link vanished */ }
            try { st.PeerCount = _device.Snapshot(n).Peers.Count; } catch { /* device vanished */ }
            st.Ready();
            PutStatus(st);
        }

        // REQ-RCN-037 — a deletion record whose link is absent has served its
        // purpose. Leaving it would keep reporting a link nobody can see.
        foreach (string n in _store.DeletionNames())
        {
            if (live.Contains(n)) continue;
            _store.ClearDeletion(n);
            lock (_mapLock) _status.Remove(n);
        }
    }

    private bool LinkPresent(string name) => _link.Names().Contains(name);

    // REQ-RES-019: the administrative flag, not the operational state, because a
    // WireGuard link has no carrier to report on.
    private static OperState OperStateOf(LinkState ls) => ls.AdminUp ? OperState.Up : OperState.Down;
}

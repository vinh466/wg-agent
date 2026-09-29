using System.Text.Json;
using WgAgent.Core.Model;
using WgAgent.Core.Reconcile;

namespace WgAgent.Core.Store;

/// <summary>
/// An exclusive handle on the desired-state file.
/// </summary>
/// <remarks>
/// <see cref="Open"/> acquires the advisory lock of REQ-RCN-006 and
/// <see cref="Dispose"/> releases it. Every write goes through
/// <see cref="Update"/>, the transaction REQ-RCN-003 requires: the whole file is
/// rendered to a temporary path and renamed over the original, so a reader sees
/// either the previous state or the next.
/// </remarks>
public sealed class Store : IReconcileStore, IDisposable
{
    private readonly string _path;
    private FileStream? _lock;
    private StoreFile _f;

    private Store(string path, FileStream lockHandle, StoreFile f)
    {
        _path = path;
        _lock = lockHandle;
        _f = f;
    }

    // A sibling of the store rather than the store itself. A lock held on the
    // store file would be lost the moment Update renamed a new file over it,
    // because the lock belongs to the inode and not to the name.
    private static string LockPath(string path)
        => Path.Combine(Path.GetDirectoryName(path) ?? ".", "." + Path.GetFileName(path) + ".lock");

    /// <summary>
    /// Takes the exclusive lock and reads the current state. An absent store file
    /// is an empty state, which is what lets a command act on a node before the
    /// agent has ever run. Throws <see cref="StoreLockedException"/> when another
    /// process holds the lock — REQ-RCN-007.
    /// </summary>
    public static Store Open(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        FileStream lockHandle;
        try
        {
            // FileShare.None maps to an exclusive advisory lock on Unix, which is
            // the flock REQ-RCN-006 requires without a child process or P/Invoke.
            lockHandle = new FileStream(
                LockPath(path), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e)
        {
            // For a command reaching the store directly, a held lock means the
            // agent is serving.
            throw new StoreLockedException(e);
        }

        try
        {
            var snap = Snapshot.Read(path);
            return new Store(path, lockHandle, snap.Data);
        }
        catch
        {
            lockHandle.Dispose();
            throw;
        }
    }

    /// <summary>A read view of the state currently held.</summary>
    public Snapshot Current() => new(_f);

    // ── Read delegation ─────────────────────────────────────────────────────
    // The holder of the lock is the process that reconciles, so it reads desired
    // state through the handle it already has. Each call reflects the last
    // committed transaction.

    public IReadOnlyList<string> Names() => Current().Names();
    public bool TryGetInterface(string name, out InterfaceSpec spec) => Current().TryGetInterface(name, out spec);
    public IReadOnlyList<Peer> Peers(string name) => Current().Peers(name);
    public bool Describes(string name) => Current().Describes(name);
    public bool DeletionRecord(string name) => Current().DeletionRecord(name);
    public IReadOnlyList<string> DeletionNames() => Current().DeletionNames();

    /// <summary>Commits the removal of one deletion record — REQ-RCN-037.</summary>
    public void ClearDeletion(string name)
    {
        if (!Current().DeletionRecord(name)) return;
        Update(t => t.ClearDeletion(name));
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> and writes the result atomically. When
    /// it throws, nothing is written, which is what makes an adoption that fails
    /// part-way leave the store unchanged — REQ-RCN-064.
    /// </summary>
    public void Update(Action<Txn> mutate)
    {
        ObjectDisposedException.ThrowIf(_lock is null, this);

        var next = _f.Clone();
        next.Schema = Snapshot.SchemaVersion;
        mutate(new Txn(next));
        WriteAtomic(_path, next);
        _f = next;
    }

    // Renders the file to a temporary path in the same directory and renames it
    // over the target. Same directory matters: rename is only atomic within a
    // filesystem. Mode 0600 is REQ-RCN-004.
    private static void WriteAtomic(string path, StoreFile f)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(f, StoreJsonContext.Default.StoreFile);
        string dir = Path.GetDirectoryName(path) ?? ".";
        string tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Path.GetRandomFileName());

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        try
        {
            using (var s = new FileStream(tmp, options))
            {
                s.Write(bytes);
                s.Write("\n"u8);
                s.Flush(flushToDisk: true); // fsync, so the bytes survive a power loss.
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Releases the lock. The kernel releases it when the process exits in any
    /// case, which is what makes a crash safe.
    /// </summary>
    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }
}

/// <summary>Another process holds the store lock — REQ-RCN-007.</summary>
public sealed class StoreLockedException(Exception inner)
    : StoreException("another process holds the store lock", inner);

/// <summary>
/// The mutable view handed to <see cref="Store.Update"/>. Nothing reaches disk
/// until Update returns without throwing.
/// </summary>
public sealed class Txn
{
    private readonly StoreFile _f;

    internal Txn(StoreFile f) => _f = f;

    public bool Describes(string name) => _f.Interfaces?.ContainsKey(name) ?? false;

    public bool AdoptionRecord(string name) => _f.Adoptions?.ContainsKey(name) ?? false;

    public bool DeletionRecord(string name) => _f.Deletions?.ContainsKey(name) ?? false;

    /// <summary>
    /// The sysctl value recorded at adoption, which REQ-FWD-024 restores on
    /// release.
    /// </summary>
    public bool TryGetForwardingBaseline(string name, out string baseline)
    {
        if (_f.Adoptions is not null && _f.Adoptions.TryGetValue(name, out var rec))
        {
            baseline = rec.ForwardingBaseline;
            return true;
        }
        baseline = "";
        return false;
    }

    /// <summary>
    /// Writes an interface spec together with the identity fields REQ-RCN-002
    /// permits the store to hold.
    /// </summary>
    public void PutInterface(string name, InterfaceSpec spec, string instanceId, string createdAt)
    {
        _f.Interfaces ??= [];
        _f.Interfaces[name] = new IfaceRecord { InstanceId = instanceId, CreatedAt = createdAt, Spec = spec };
    }

    /// <summary>
    /// Inserts or replaces one peer, keyed by public key. REQ-RES-020 makes the
    /// pair (interface_name, public_key) the identity.
    /// </summary>
    public void PutPeer(string name, Peer p)
    {
        var peers = new List<Peer>(Peers(name));
        for (int i = 0; i < peers.Count; i++)
        {
            if (peers[i].PublicKey == p.PublicKey)
            {
                peers[i] = p;
                PutPeers(name, peers);
                return;
            }
        }
        peers.Add(p);
        PutPeers(name, peers);
    }

    /// <summary>
    /// Drops one peer. Returns whether it was there, which REQ-API-070 turns into
    /// PEER_NOT_FOUND.
    /// </summary>
    public bool RemovePeer(string name, string publicKey)
    {
        var peers = Peers(name);
        var kept = new List<Peer>(peers.Count);
        bool found = false;
        foreach (var p in peers)
        {
            if (p.PublicKey == publicKey) { found = true; continue; }
            kept.Add(p);
        }
        if (!found) return false;
        PutPeers(name, kept);
        return true;
    }

    /// <summary>Replaces the peer set of one interface.</summary>
    public void PutPeers(string name, IReadOnlyList<Peer> peers)
    {
        _f.Peers ??= [];
        if (peers.Count == 0)
        {
            _f.Peers.Remove(name);
            return;
        }
        _f.Peers[name] = [.. peers];
    }

    /// <summary>The stored peers of one interface.</summary>
    public IReadOnlyList<Peer> Peers(string name)
        => _f.Peers is not null && _f.Peers.TryGetValue(name, out var peers) ? peers : [];

    /// <summary>
    /// Records that link removal did not complete, which REQ-RCN-033 requires and
    /// REQ-RCN-034 reads to call the interface ORPHANED.
    /// </summary>
    public void PutDeletion(string name, string at)
    {
        _f.Deletions ??= [];
        _f.Deletions[name] = new DeletionRecord { RecordedAt = at };
    }

    /// <summary>Drops the record, which REQ-RCN-037 requires once the link is absent.</summary>
    public void ClearDeletion(string name) => _f.Deletions?.Remove(name);

    /// <summary>
    /// Records that the interface was adopted, carrying the forwarding sysctl
    /// value REQ-FWD-024 restores — REQ-RCN-070.
    /// </summary>
    public void PutAdoption(string name, string forwardingBaseline, string at)
    {
        _f.Adoptions ??= [];
        _f.Adoptions[name] = new AdoptionRecord { AdoptedAt = at, ForwardingBaseline = forwardingBaseline };
    }

    /// <summary>
    /// Removes an interface's spec, its peers and its adoption record. REQ-RCN-071
    /// requires the record cleared in the same transaction that removes the spec,
    /// and REQ-RCN-038 requires the same of the peers, so one method covers
    /// release and DeleteInterface alike.
    /// </summary>
    public void RemoveInterface(string name)
    {
        _f.Interfaces?.Remove(name);
        _f.Peers?.Remove(name);
        _f.Adoptions?.Remove(name);
    }
}

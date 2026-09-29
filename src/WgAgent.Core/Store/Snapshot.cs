using System.Text.Json;
using WgAgent.Core.Model;
using WgAgent.Core.Validation;
using WgAgent.Platform;

namespace WgAgent.Core.Store;

/// <summary>
/// An immutable read of desired state, implementing the ports the diagnostics
/// and validation layers read through.
/// </summary>
/// <remarks>
/// The read side is what <c>doctor</c> needs under REQ-CLI-004: it reads the
/// store directly rather than through the API, so the command works on a node
/// where the agent has never started.
/// </remarks>
public sealed class Snapshot : IDesiredState, IDesired
{
    /// <summary>The schema version this build understands — REQ-RCN-005.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Matches store.path in the SPEC-09 configuration sample.</summary>
    public const string DefaultPath = "/var/lib/wg-agent/state.db";

    private readonly StoreFile _f;

    internal Snapshot(StoreFile f) => _f = f;

    /// <summary>
    /// Loads a snapshot from a path.
    /// </summary>
    /// <remarks>
    /// An absent file yields an empty snapshot and no error. That is the ordinary
    /// case for <c>doctor</c>: before the first write there is no desired state,
    /// so every WireGuard link on the host is FOREIGN, which is exactly the set
    /// the readiness report of REQ-DIA-040 covers.
    /// </remarks>
    public static Snapshot Read(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new Snapshot(new StoreFile { Schema = SchemaVersion });
        }

        StoreFile f;
        try
        {
            f = JsonSerializer.Deserialize(bytes, StoreJsonContext.Default.StoreFile)
                ?? throw new StoreException($"store {path} is empty JSON");
        }
        catch (JsonException e)
        {
            throw new StoreException($"parse store {path}: {e.Message}", e);
        }

        if (f.Schema > SchemaVersion)
            throw new SchemaTooNewException(f.Schema, SchemaVersion);

        return new Snapshot(f);
    }

    internal StoreFile Data => _f;

    /// <summary>
    /// Whether desired state describes the interface — REQ-RES-017's MANAGED.
    /// </summary>
    public bool Describes(string name) => _f.Interfaces?.ContainsKey(name) ?? false;

    /// <summary>
    /// Whether a deletion record names the interface — REQ-RCN-034's ORPHANED.
    /// </summary>
    public bool DeletionRecord(string name) => _f.Deletions?.ContainsKey(name) ?? false;

    /// <summary>Alias matching <see cref="IDesiredState.HasDeletionRecord"/>.</summary>
    public bool HasDeletionRecord(string name) => DeletionRecord(name);

    /// <summary>
    /// Whether an adoption record names the interface — REQ-RCN-072 tests this
    /// before permitting a release.
    /// </summary>
    public bool AdoptionRecord(string name) => _f.Adoptions?.ContainsKey(name) ?? false;

    /// <summary>
    /// The interfaces desired state describes, sorted — the set REQ-RCN-022
    /// iterates.
    /// </summary>
    public IReadOnlyList<string> Names()
    {
        if (_f.Interfaces is null) return [];
        var names = new List<string>(_f.Interfaces.Keys);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>
    /// One stored spec. Returns false when desired state does not describe it —
    /// REQ-RES-017's MANAGED test.
    /// </summary>
    public bool TryGetInterface(string name, out InterfaceSpec spec)
    {
        if (_f.Interfaces is not null && _f.Interfaces.TryGetValue(name, out var rec))
        {
            spec = rec.Spec;
            return true;
        }
        spec = new InterfaceSpec();
        return false;
    }

    /// <summary>
    /// The instance_id and created_at REQ-RES-018 assigns. They sit beside the
    /// spec rather than inside it, per REQ-RES-034.
    /// </summary>
    public bool TryGetIdentity(string name, out string instanceId, out string createdAt)
    {
        if (_f.Interfaces is not null && _f.Interfaces.TryGetValue(name, out var rec))
        {
            instanceId = rec.InstanceId;
            createdAt = rec.CreatedAt;
            return true;
        }
        instanceId = "";
        createdAt = "";
        return false;
    }

    /// <summary>The stored peers of one interface.</summary>
    public IReadOnlyList<Peer> Peers(string name)
        => _f.Peers is not null && _f.Peers.TryGetValue(name, out var peers) ? peers : [];

    /// <summary>
    /// The interfaces a deletion record names, sorted. REQ-RCN-037 requires each
    /// to be cleared once its link is absent.
    /// </summary>
    public IReadOnlyList<string> DeletionNames()
    {
        if (_f.Deletions is null) return [];
        var names = new List<string>(_f.Deletions.Keys);
        names.Sort(StringComparer.Ordinal);
        return names;
    }
}

/// <summary>A store read or parse fault.</summary>
public class StoreException : Exception
{
    public StoreException(string message) : base(message) { }
    public StoreException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A store written by a newer build — REQ-RCN-005.</summary>
public sealed class SchemaTooNewException(int found, int understood)
    : StoreException($"store schema is newer than this build understands: file is {found}, this build understands {understood}")
{
    public int Found { get; } = found;
    public int Understood { get; } = understood;
}

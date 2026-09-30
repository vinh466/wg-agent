using System.Text.Json;
using WgAgent.Core.Model;
using WgAgent.Platform;

namespace WgAgent.Core.Store;

/// <summary>
/// Desired state on disk (REQ-RCN-001): one JSON file, replaced atomically on every write
/// (REQ-RCN-003), mode 0600 and owned by the account that writes it (REQ-RCN-004). Every write
/// runs under the lock of <see cref="StoreLock"/> (REQ-RCN-042), which the caller holds.
/// </summary>
public sealed class StateStore(string path)
{
    public const int SchemaVersion = 1;
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public string Path { get; } = path;

    /// <summary>The lock file beside the store — never the store itself, which a rename replaces.</summary>
    public string LockPath => Path + ".lock";

    /// <summary>Reads the store; an absent file is an empty store, the state of a new node.</summary>
    public StoreState Load()
    {
        if (!File.Exists(Path)) return StoreState.Empty;
        StoreFile? file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllBytes(Path), StoreJsonContext.Default.StoreFile);
        }
        catch (JsonException e)
        {
            throw new AgentException(ReasonCodes.StoreCorrupt, $"The store at {Path} cannot be read: {e.Message}");
        }
        if (file is null || file.SchemaVersion < 1)
            throw new AgentException(ReasonCodes.StoreCorrupt, $"The store at {Path} has no valid schema version.");

        // REQ-RCN-005
        if (file.SchemaVersion > SchemaVersion)
            throw new AgentException(ReasonCodes.StoreSchemaTooNew,
                $"The store at {Path} is schema version {file.SchemaVersion}; this build understands up to version {SchemaVersion}. It was written by a newer agent.");

        return new StoreState(file.Interfaces.ToDictionary(e => e.Key, e => FromFile(e.Value)));
    }

    /// <summary>
    /// Writes the whole state: serialised in memory first, then written to a temporary file in the
    /// same directory, synced, and renamed over the store, so a failure at any step leaves the
    /// previous store intact.
    /// </summary>
    public void Save(StoreState state)
    {
        var file = new StoreFile
        {
            SchemaVersion = SchemaVersion,
            Interfaces = state.Interfaces.ToDictionary(e => e.Key, e => ToFile(e.Value)),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, StoreJsonContext.Default.StoreFile);

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
                   { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = OwnerOnly }))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    private static StoreInterface ToFile(StoredInterface stored) => new()
    {
        CreatedAt = stored.CreatedAt,
        Spec = new StoreInterfaceSpec
        {
            PrivateKey = stored.Spec.PrivateKey?.Reveal(),
            ListenPort = stored.Spec.ListenPort ?? Defaults.ListenPort,
            Addresses = [.. stored.Spec.Addresses ?? []],
            Mtu = stored.Spec.Mtu ?? Defaults.Mtu,
            Enabled = stored.Spec.Enabled ?? Defaults.Enabled,
            Labels = new Dictionary<string, string>(stored.Spec.Labels ?? new Dictionary<string, string>()),
            PostUp = [.. stored.Spec.PostUp ?? []],
            PostDown = [.. stored.Spec.PostDown ?? []],
        },
        Peers = stored.Peers.ToDictionary(p => p.Key, p => new StorePeerSpec
        {
            PresharedKey = p.Value.PresharedKey?.Reveal(),
            AllowedIps = [.. p.Value.AllowedIps ?? []],
            Endpoint = p.Value.Endpoint,
            PersistentKeepalive = p.Value.PersistentKeepalive ?? Defaults.PersistentKeepalive,
            Labels = new Dictionary<string, string>(p.Value.Labels ?? new Dictionary<string, string>()),
        }),
    };

    private static StoredInterface FromFile(StoreInterface file) => new(
        new InterfaceSpec
        {
            PrivateKey = file.Spec.PrivateKey is null ? null : SecretKey.Parse(file.Spec.PrivateKey),
            ListenPort = file.Spec.ListenPort,
            Addresses = file.Spec.Addresses,
            Mtu = file.Spec.Mtu,
            Enabled = file.Spec.Enabled,
            Labels = file.Spec.Labels,
            PostUp = file.Spec.PostUp,
            PostDown = file.Spec.PostDown,
        },
        file.CreatedAt,
        file.Peers.ToDictionary(p => p.Key, p => new PeerSpec
        {
            PresharedKey = p.Value.PresharedKey is null ? null : SecretKey.Parse(p.Value.PresharedKey),
            AllowedIps = p.Value.AllowedIps,
            Endpoint = p.Value.Endpoint,
            PersistentKeepalive = p.Value.PersistentKeepalive,
            Labels = p.Value.Labels,
        }));
}

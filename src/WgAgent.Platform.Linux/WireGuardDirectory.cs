namespace WgAgent.Platform.Linux;

/// <summary>
/// <see cref="IConfigDirectory"/> over /etc/wireguard/. A file is written to a temporary file in the
/// same directory, mode 0600, synced and renamed over the old one, so wg-quick never reads half a
/// file and the old one stays whole until the rename (REQ-APL-009). The process runs as root, so the
/// file is root's.
/// </summary>
public sealed class WireGuardDirectory(string directory = "/etc/wireguard") : IConfigDirectory
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public IReadOnlySet<string> Names() => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "*.conf").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet()
        : new HashSet<string>();

    public string? Read(string interfaceName)
    {
        var path = PathOf(interfaceName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void Write(string interfaceName, string content)
    {
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = Path.Combine(directory, $".{interfaceName}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
                   { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = OwnerOnly }))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, PathOf(interfaceName), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.Delete(temporary);
            throw new PlatformException($"Writing {PathOf(interfaceName)} failed: {e.Message}");
        }
    }

    public void Delete(string interfaceName) => File.Delete(PathOf(interfaceName));

    private string PathOf(string interfaceName) => Path.Combine(directory, interfaceName + ".conf");
}

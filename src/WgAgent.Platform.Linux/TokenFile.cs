using WgAgent.Platform;

namespace WgAgent.Platform.Linux;

/// <summary>
/// The one-line file holding the bearer token (REQ-CFG-003). It is read for authentication and
/// replaced by <c>token rotate</c>; a file readable beyond its owner (REQ-SEC-074) or owned by
/// another account (REQ-SEC-082) is refused, so root reading it does not mask the wrong trust.
/// </summary>
public sealed class TokenFile(string path)
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public string Path { get; } = path;

    /// <summary>A token file that cannot be trusted or read.</summary>
    public sealed class Invalid(string message) : Exception(message);

    /// <summary>
    /// The token, or null when the file is absent or empty. Throws <see cref="Invalid"/> when the
    /// file grants access beyond its owner or is owned by another account.
    /// </summary>
    public Secret? Read()
    {
        if (!File.Exists(Path)) return null;

        var mode = File.GetUnixFileMode(Path);
        if ((mode & ~OwnerOnly) != 0)   // REQ-SEC-074
            throw new Invalid($"The token file {Path} is mode {ModeString(mode)}; it must grant no access beyond its owner.");

        var owner = Unix.OwnerUserId(Path);
        var self = Unix.EffectiveUserId();
        if (owner != self)   // REQ-SEC-082
            throw new Invalid($"The token file {Path} is owned by uid {owner}, not by the agent's uid {self}.");

        var text = File.ReadAllText(Path).Trim();
        return text.Length == 0 ? null : Secret.From(text);
    }

    /// <summary>
    /// Replaces the file atomically, mode 0600, owned by the writer (REQ-CLI-013, REQ-CLI-014): a
    /// failure leaves the previous contents intact.
    /// </summary>
    public void Write(Secret token)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
                   { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = OwnerOnly }))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(token.Reveal() + "\n"));
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

    private static string ModeString(UnixFileMode mode) =>
        ((int)mode).ToString("000", System.Globalization.CultureInfo.InvariantCulture);
}

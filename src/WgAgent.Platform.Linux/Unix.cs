using System.Runtime.InteropServices;

namespace WgAgent.Platform.Linux;

/// <summary>
/// The two facts a token file's ownership check needs (REQ-SEC-082), read through libc rather than a
/// child process (REQ-SEC-087). glibc on the supported distributions exports <c>stat</c> directly.
/// </summary>
internal static partial class Unix
{
    /// <summary>The effective user id of the running agent.</summary>
    public static uint EffectiveUserId() => geteuid();

    /// <summary>The owner's user id of a file that exists.</summary>
    public static uint OwnerUserId(string path)
    {
        if (stat(path, out var buffer) != 0)
            throw new IOException($"stat({path}) failed with errno {Marshal.GetLastPInvokeError()}.");
        return buffer.Uid;
    }

    // struct stat, x86-64 Linux: st_uid is at offset 28; the whole struct is 144 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct StatBuffer
    {
        [FieldOffset(28)] public uint Uid;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial uint geteuid();

    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int stat(string path, out StatBuffer buffer);
}

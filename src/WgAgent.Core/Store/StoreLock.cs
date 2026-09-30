using System.Diagnostics;

namespace WgAgent.Core.Store;

/// <summary>
/// The one exclusive lock every writer takes (REQ-RCN-042): an advisory lock on a file that is
/// never renamed. A writer waits for it, and gives up after the timeout (REQ-RCN-075). The kernel
/// releases the lock when its holder exits, so a crash cannot leave it held.
/// </summary>
public sealed class StoreLock : IDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);
    private readonly FileStream _handle;

    private StoreLock(FileStream handle) => _handle = handle;

    public static StoreLock Acquire(string lockPath, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                // FileShare.None is an exclusive flock(2) on Linux.
                return new StoreLock(new FileStream(lockPath, new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                }));
            }
            catch (IOException) when (clock.Elapsed < timeout)
            {
                Thread.Sleep(Poll);
            }
            catch (IOException)
            {
                throw new AgentException(ReasonCodes.StoreBusy,
                    $"Another writer has held the lock {lockPath} for longer than {timeout.TotalSeconds:0.#} s.");
            }
        }
    }

    public void Dispose() => _handle.Dispose();
}

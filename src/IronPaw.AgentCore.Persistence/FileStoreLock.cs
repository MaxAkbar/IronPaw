using System.Diagnostics;

namespace IronPaw.AgentCore.Persistence;

internal static class FileStoreLock
{
    public static async Task<FileStream> AcquireAsync(string storePath, CancellationToken ct)
    {
        var lockPath = storePath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Keep the lock file after closing it: deleting it would let another
                // process open a different inode while an existing owner holds its lock.
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(25, ct);
            }
        }
    }
}

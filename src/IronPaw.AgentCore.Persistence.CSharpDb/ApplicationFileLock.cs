using System.Diagnostics;

namespace IronPaw.AgentCore.Persistence.CSharpDb;

internal static class ApplicationFileLock
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static async Task<FileStream> AcquireAsync(string databasePath, CancellationToken ct)
    {
        var path = databasePath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Never unlink this file: waiters must all lock the same inode.
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsContention(ex))
            {
                if (elapsed.Elapsed >= Timeout)
                    throw new TimeoutException($"Application storage is busy: could not acquire '{path}' within 30 seconds. Close another IronPaw instance holding this lock and retry.", ex);
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsContention(IOException ex) => (ex.HResult & 0xffff) is 32 or 33 or 11;
}

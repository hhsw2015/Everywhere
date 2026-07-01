using System.Diagnostics;

namespace Everywhere.Mcp.OpenCli.Memory;

/// <summary>
/// SPEC §Phase 3 concurrency — exclusive per-file lock via
/// <c>&lt;file&gt;.lock</c> sentinel, 5s timeout → <c>MEMORY_LOCK_TIMEOUT</c>.
/// Atomic write: tmp file then <c>File.Move</c>.
/// </summary>
public static class MergeSafeWriter
{
    private const int LockTimeoutMs = 5000;

    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/>. Fails MEMORY_LOCK_TIMEOUT if the lock can't be acquired in 5s.</summary>
    public static void WriteAtomic(string path, string content)
        => MergeAtomic(path, () => content);

    /// <summary>
    /// Acquires the per-path lock, invokes <paramref name="produce"/> to
    /// build the final content (which may throw <see cref="MergeConflictException"/>
    /// after re-reading the current state under the lock), and writes atomically.
    /// This closes the read-then-write TOCTOU that plain <see cref="WriteAtomic"/> leaves open.
    /// </summary>
    public static void MergeAtomic(string path, Func<string> produce)
    {
        var lockPath = path + ".lock";
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var sw = Stopwatch.StartNew();
        FileStream? lockHandle = null;
        while (true)
        {
            try
            {
                lockHandle = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                break;
            }
            catch (IOException)
            {
                if (sw.ElapsedMilliseconds > LockTimeoutMs)
                    throw new MemoryLockTimeoutException(path, (int)sw.ElapsedMilliseconds);
                Thread.Sleep(25);
            }
        }
        try
        {
            var content = produce();
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            // .NET 6+: Move(overwrite:true) is atomic on POSIX (rename(2)),
            // and closes the read-then-move-then-crash-loses-the-file window.
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            lockHandle?.Dispose();
            try { File.Delete(lockPath); } catch { /* best-effort */ }
        }
    }
}

public sealed class MemoryLockTimeoutException(string path, int waitedMs)
    : Exception($"MEMORY_LOCK_TIMEOUT: {path} (waited {waitedMs}ms)")
{
    public string Path { get; } = path;
    public int WaitedMs { get; } = waitedMs;
}

public sealed class MergeConflictException(string path, string existingHash)
    : Exception($"MERGE_CONFLICT: {path}")
{
    public string Path { get; } = path;
    public string ExistingHash { get; } = existingHash;
}

public sealed class PathTraversalException(string attempted, string resolved)
    : Exception($"PATH_TRAVERSAL: '{attempted}' resolved outside sites/")
{
    public string Attempted { get; } = attempted;
    public string Resolved { get; } = resolved;
}

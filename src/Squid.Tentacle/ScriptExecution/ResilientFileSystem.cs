using Serilog;

namespace Squid.Tentacle.ScriptExecution;

public static class ResilientFileSystem
{
    private const int MaxRetries = 10;

    public static void WriteAllText(string path, string contents)
        => ExecuteWithRetry(() => File.WriteAllText(path, contents), path);

    /// <summary>
    /// Atomically writes <paramref name="contents"/> to <paramref name="path"/>.
    /// A mid-write crash cannot produce a partial/corrupt target file — either
    /// the old content survives or the new content is fully present.
    ///
    /// On success the previous version (if any) is retained as <c>{path}.bak</c>
    /// which callers can fall back to if the primary file ever becomes unreadable.
    /// </summary>
    public static void AtomicWriteAllText(string path, string contents)
        => ExecuteWithRetry(() => AtomicWriteCore(path, contents), path);

    private static void AtomicWriteCore(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Unique tmp per invocation so concurrent writers to the same target don't collide
        // on the staging file. File.Replace itself is atomic at the OS level, so the
        // last-writer-wins semantic is preserved without IOException races.
        var tempPath = $"{path}.{Environment.CurrentManagedThreadId}.{Guid.NewGuid():N}.tmp";
        var backupPath = path + ".bak";

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            }
        }
    }

    public static string ReadAllText(string path)
        => ExecuteWithRetry(() => File.ReadAllText(path), path);

    public static void Move(string sourceFileName, string destFileName, bool overwrite)
        => ExecuteWithRetry(() => File.Move(sourceFileName, destFileName, overwrite), destFileName);

    public static void DeleteFile(string path)
        => ExecuteWithRetry(() => File.Delete(path), path);

    public static bool FileExists(string path)
        => ExecuteWithRetry(() => File.Exists(path), path);

    public static void CreateDirectory(string path)
        => ExecuteWithRetry(() => Directory.CreateDirectory(path), path);

    public static bool DirectoryExists(string path)
        => ExecuteWithRetry(() => Directory.Exists(path), path);

    public static void DeleteDirectory(string path, bool recursive)
        => ExecuteWithRetry(() => Directory.Delete(path, recursive), path);

    public static string[] GetDirectories(string path)
        => ExecuteWithRetry(() => Directory.GetDirectories(path), path);

    public static byte[] ReadAllBytes(string path)
        => ExecuteWithRetry(() => File.ReadAllBytes(path), path);

    public static void WriteAllBytes(string path, byte[] bytes)
        => ExecuteWithRetry(() => File.WriteAllBytes(path, bytes), path);

    public static string[] GetFiles(string path)
        => ExecuteWithRetry(() => Directory.GetFiles(path), path);

    public static string[] GetFiles(string path, string searchPattern)
        => ExecuteWithRetry(() => Directory.GetFiles(path, searchPattern), path);

    public static void SetUnixFileMode(string path, UnixFileMode mode)
    {
        ExecuteWithRetry(() =>
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, mode);
        }, path);
    }

    private static void ExecuteWithRetry(Action action, string path)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex) when (attempt < MaxRetries && IsTransientIoError(ex))
            {
                var delay = CalculateDelay(attempt);

                Log.Warning("Filesystem operation failed on {Path} (attempt {Attempt}/{MaxRetries}), retrying in {DelayMs}ms: {Message}",
                    path, attempt, MaxRetries, delay, ex.Message);

                Thread.Sleep(delay);
            }
        }
    }

    private static T ExecuteWithRetry<T>(Func<T> func, string path)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return func();
            }
            catch (IOException ex) when (attempt < MaxRetries && IsTransientIoError(ex))
            {
                var delay = CalculateDelay(attempt);

                Log.Warning("Filesystem operation failed on {Path} (attempt {Attempt}/{MaxRetries}), retrying in {DelayMs}ms: {Message}",
                    path, attempt, MaxRetries, delay, ex.Message);

                Thread.Sleep(delay);
            }
        }

        return func();
    }

    internal static int CalculateDelay(int attempt)
        => attempt * attempt * 100;

    private static bool IsTransientIoError(IOException ex)
    {
        if (ex is FileNotFoundException or DirectoryNotFoundException)
            return false;

        return true;
    }
}

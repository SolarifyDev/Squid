using Serilog;

namespace Squid.Tentacle.ScriptExecution;

public static class ResilientFileSystem
{
    private const int MaxRetries = 10;

    public static void WriteAllText(string path, string contents)
        => ExecuteWithRetry(() => File.WriteAllText(path, contents), path);

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

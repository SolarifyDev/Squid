namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Writes a file so that it is either fully replaced or left as it was: the content goes to a temporary file beside it, which is renamed
/// over the target only once complete. A write cut off part way (a deadline, a full disk) never leaves a truncated file behind.
/// The temporary name is unique per write and never ends in the target's extension, so directory scans for the target kind skip it.
/// </summary>
public static class OctopusImportAtomicFile
{
    public static async Task WriteAsync(string path, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        var partial = $"{path}.{Guid.NewGuid():N}.partial";

        try
        {
            await WritePartialAsync(partial, write, ct).ConfigureAwait(false);

            File.Move(partial, path, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static async Task WritePartialAsync(string partial, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        await using var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        await write(file, ct).ConfigureAwait(false);
    }

    private static void TryDelete(string partial)
    {
        try
        {
            File.Delete(partial);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the original failure is the one to report.
        }
    }
}

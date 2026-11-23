using Serilog;

namespace Squid.Tentacle.ScriptExecution.State;

/// <summary>
/// Used when a finished script's workspace cannot be released because its result could not be recorded anywhere else
/// (the completion tombstone failed to write): the persisted state must stay, but the script, its variables and any
/// sensitive files must not.
/// </summary>
internal static class StateOnlyWorkspace
{
    private const string StateFilePrefix = "scriptstate.json";

    public static void Scrub(string workDir)
    {
        try
        {
            if (!Directory.Exists(workDir)) return;

            foreach (var entry in Directory.EnumerateFileSystemEntries(workDir).Where(e => !IsStateFile(e)))
                DeleteEntry(entry);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to scrub workspace {WorkDir} down to its state file", workDir);
        }
    }

    private static bool IsStateFile(string path) => Path.GetFileName(path).StartsWith(StateFilePrefix, StringComparison.Ordinal);

    private static void DeleteEntry(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }
}

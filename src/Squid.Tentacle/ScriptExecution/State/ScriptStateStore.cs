using System.Text.Json;
using Serilog;

namespace Squid.Tentacle.ScriptExecution.State;

/// <summary>
/// Disk-backed, crash-safe persistence for <see cref="ScriptState"/>.
///
/// Writes are atomic via the three-file dance:
///   1. Serialize JSON to <c>scriptstate.json.tmp</c>
///   2. <c>File.Replace(tmp, target, backup)</c> — OS-level atomic swap
///      that keeps the previous good version as <c>scriptstate.json.bak</c>
///
/// If the primary file is corrupted (partial write, disk fault), <see cref="Load"/>
/// transparently falls back to the backup. This matches the Octopus Tentacle
/// ScriptStateStore guarantees: no state loss on power cut, kernel panic,
/// or mid-write process kill.
/// </summary>
public sealed class ScriptStateStore : IScriptStateStore
{
    private const string StateFileName = "scriptstate.json";
    private const string BackupFileName = "scriptstate.json.bak";
    private const string TempFileName = "scriptstate.json.tmp";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _workspace;
    private readonly object _ioLock = new();

    public ScriptStateStore(string workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public string WorkspacePath => _workspace;

    private string StatePath => Path.Combine(_workspace, StateFileName);
    private string BackupPath => Path.Combine(_workspace, BackupFileName);
    private string TempPath => Path.Combine(_workspace, TempFileName);

    public bool Exists()
    {
        lock (_ioLock)
            return File.Exists(StatePath) || File.Exists(BackupPath);
    }

    public ScriptState Load()
    {
        lock (_ioLock)
        {
            if (TryLoadFrom(StatePath, out var primary))
                return primary!;

            if (TryLoadFrom(BackupPath, out var backup))
            {
                Log.Warning("Primary script state file at {Path} was unreadable; recovered from backup", StatePath);
                return backup!;
            }

            throw new InvalidOperationException($"No readable script state at {_workspace}");
        }
    }

    public void Save(ScriptState state)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));

        lock (_ioLock)
        {
            Directory.CreateDirectory(_workspace);
            WriteTempFile(state);
            AtomicReplace();
        }
    }

    public void Delete()
    {
        lock (_ioLock)
        {
            TryDelete(StatePath);
            TryDelete(BackupPath);
            TryDelete(TempPath);
        }
    }

    private void WriteTempFile(ScriptState state)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions);

        using var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);

        writer.Write(json);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private void AtomicReplace()
    {
        if (File.Exists(StatePath))
            File.Replace(TempPath, StatePath, BackupPath, ignoreMetadataErrors: true);
        else
            File.Move(TempPath, StatePath);
    }

    private static bool TryLoadFrom(string path, out ScriptState? state)
    {
        state = null;

        if (!File.Exists(path)) return false;

        try
        {
            var json = File.ReadAllText(path);
            state = JsonSerializer.Deserialize<ScriptState>(json, JsonOptions);
            return state != null;
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Corrupted script state at {Path}", path);
            return false;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to read script state at {Path}", path);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException ex) { Log.Debug(ex, "Failed to delete {Path} — will retry at next cleanup", path); }
    }
}

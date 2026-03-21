using System.Text.Json;
using Serilog;
using RFS = Squid.Tentacle.ScriptExecution.ResilientFileSystem;

namespace Squid.Tentacle.ScriptExecution;

public class ScriptStateFile
{
    public string TicketId { get; set; } = string.Empty;
    public string PodName { get; set; } = string.Empty;
    public string EosMarkerToken { get; set; } = string.Empty;
    public string Isolation { get; set; } = string.Empty;
    public string? IsolationMutexName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    private const string FileName = ".squid-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string GetPath(string workDir) => Path.Combine(workDir, FileName);

    public static void Write(string workDir, ScriptStateFile state)
    {
        var path = GetPath(workDir);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        RFS.WriteAllText(path, json);

        Log.Debug("Wrote script state file for ticket {TicketId} to {Path}", state.TicketId, path);
    }

    public static ScriptStateFile? TryRead(string workDir)
    {
        var path = GetPath(workDir);

        if (!RFS.FileExists(path)) return null;

        try
        {
            var json = RFS.ReadAllText(path);
            return JsonSerializer.Deserialize<ScriptStateFile>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read script state file at {Path}", path);
            return null;
        }
    }
}

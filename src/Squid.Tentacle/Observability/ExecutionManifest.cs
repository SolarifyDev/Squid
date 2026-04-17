using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Tentacle.Observability;

/// <summary>
/// Per-script execution manifest written into the workspace directory. Records
/// everything needed to reproduce or audit a script run: script body hash,
/// file hashes, isolation, exit code, agent version, timing. Persisted next
/// to the state file so a post-mortem ("why did this deployment fail on host
/// X 3 days ago?") can replay the exact payload the agent saw.
///
/// Written atomically on completion — not on every status poll — so it reflects
/// a final, coherent snapshot. File name is <c>execution-manifest.json</c>.
/// </summary>
public sealed class ExecutionManifest
{
    public string TicketId { get; init; } = string.Empty;
    public string ScriptBodyHash { get; init; } = string.Empty;
    public Dictionary<string, string> FileHashes { get; init; } = new();
    public string Isolation { get; init; } = string.Empty;
    public string IsolationMutexName { get; init; } = string.Empty;
    public int? ExitCode { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string AgentVersion { get; init; } = string.Empty;
    public string ScriptType { get; init; } = string.Empty;
    public string? TraceId { get; init; }

    public const string FileName = "execution-manifest.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Build a manifest. File hashes are populated after materialization, so
    /// callers pass a <paramref name="workspace"/> — we read the already-written
    /// files from disk and hash there, avoiding the DataStream one-shot issue.
    /// If <paramref name="workspace"/> is null, file hashes are left empty.
    /// </summary>
    public static ExecutionManifest Build(string ticketId, StartScriptCommand command, string agentVersion, DateTimeOffset startedAt, int? exitCode, DateTimeOffset? completedAt, string? traceId = null, string? workspace = null)
    {
        return new ExecutionManifest
        {
            TicketId = ticketId,
            ScriptBodyHash = HashText(command.ScriptBody ?? string.Empty),
            FileHashes = HashFilesOnDisk(command, workspace),
            Isolation = command.Isolation.ToString(),
            IsolationMutexName = command.IsolationMutexName ?? string.Empty,
            ExitCode = exitCode,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            AgentVersion = agentVersion ?? string.Empty,
            ScriptType = command.ScriptSyntax.ToString(),
            TraceId = traceId
        };
    }

    private static Dictionary<string, string> HashFilesOnDisk(StartScriptCommand command, string? workspace)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(workspace)) return result;

        foreach (var file in command.Files)
        {
            var path = Path.Combine(workspace, file.Name);
            if (!File.Exists(path))
            {
                result[file.Name] = "sha256:missing";
                continue;
            }
            try
            {
                using var fs = File.OpenRead(path);
                var hash = SHA256.HashData(fs);
                result[file.Name] = "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to hash {Path} for execution manifest", path);
                result[file.Name] = "sha256:error";
            }
        }
        return result;
    }

    public void WriteTo(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return;
        try
        {
            Directory.CreateDirectory(workspace);
            var json = JsonSerializer.Serialize(this, Options);
            var path = Path.Combine(workspace, FileName);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to write execution manifest for ticket {TicketId}", TicketId);
        }
    }

    public static ExecutionManifest? TryRead(string workspace)
    {
        try
        {
            var path = Path.Combine(workspace, FileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ExecutionManifest>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to read execution manifest at {Workspace}", workspace);
            return null;
        }
    }

    internal static string HashText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}

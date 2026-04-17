using Serilog;

namespace Squid.Tentacle.ScriptExecution.Logging;

/// <summary>
/// Reads sequenced log entries strictly greater than a caller-supplied cursor.
///
/// The reader opens the file with <see cref="FileShare.ReadWrite"/> so a live
/// writer can keep appending — every call returns a snapshot of what is
/// currently on disk up to a whole-line boundary. Malformed or truncated lines
/// (e.g. a mid-write crash) are skipped so the caller always sees valid entries.
/// </summary>
public sealed class SequencedLogReader
{
    private readonly string _path;

    public SequencedLogReader(string logFilePath)
    {
        if (string.IsNullOrWhiteSpace(logFilePath))
            throw new ArgumentException("Log file path required", nameof(logFilePath));
        _path = logFilePath;
    }

    public IReadOnlyList<SequencedLogEntry> ReadFrom(long afterSequence)
    {
        if (!File.Exists(_path)) return Array.Empty<SequencedLogEntry>();

        var entries = new List<SequencedLogEntry>();

        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!SequencedLogFormat.TryDecode(line, out var entry)) continue;
                if (entry.Sequence <= afterSequence) continue;
                entries.Add(entry);
            }
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to read sequenced log at {Path}", _path);
        }

        return entries;
    }

    public long GetHighestSequence()
    {
        if (!File.Exists(_path)) return -1;

        long highest = -1;
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
                if (SequencedLogFormat.TryDecode(line, out var entry))
                    highest = Math.Max(highest, entry.Sequence);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to scan sequenced log at {Path}", _path);
        }
        return highest;
    }
}

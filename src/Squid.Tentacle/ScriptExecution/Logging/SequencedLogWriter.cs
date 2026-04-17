using System.Text;
using Squid.Message.Contracts.Tentacle;
using Serilog;

namespace Squid.Tentacle.ScriptExecution.Logging;

/// <summary>
/// Append-only, sequence-numbered log writer backed by a workspace file.
///
/// Two design goals:
///   1. <b>Survive agent restart</b>: every line is flushed to disk so a reader
///      can replay everything written before the crash.
///   2. <b>Safe concurrent producers</b>: stdout and stderr are written from
///      different async callbacks — all writes go through a single lock so
///      lines don't interleave, and sequence numbers are strictly increasing.
///
/// The writer recovers its starting sequence number from any existing log file,
/// so a fresh <see cref="SequencedLogWriter"/> opened on an existing workspace
/// continues the previous sequence rather than restarting at 0.
/// </summary>
public sealed class SequencedLogWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly object _sync = new();
    private long _nextSequence;
    private int _disposed;

    public SequencedLogWriter(string logFilePath)
    {
        if (string.IsNullOrWhiteSpace(logFilePath))
            throw new ArgumentException("Log file path required", nameof(logFilePath));

        var directory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _nextSequence = DetermineStartSequence(logFilePath);
        _stream = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, FileOptions.WriteThrough);
        _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public long NextSequence
    {
        get { lock (_sync) return _nextSequence; }
    }

    public long Append(ProcessOutputSource source, string text, DateTimeOffset? occurred = null)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));

        lock (_sync)
        {
            var seq = _nextSequence++;
            var entry = new SequencedLogEntry(seq, occurred ?? DateTimeOffset.UtcNow, source, text);
            _writer.Write(SequencedLogFormat.Encode(entry));
            _writer.Write(SequencedLogFormat.LineSeparator);
            _writer.Flush();
            return seq;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _writer.Dispose(); }
        catch (IOException ex) { Log.Debug(ex, "Failed to close log writer"); }
    }

    private static long DetermineStartSequence(string path)
    {
        if (!File.Exists(path)) return 0;

        try
        {
            var lastLine = ReadLastLine(path);
            if (string.IsNullOrEmpty(lastLine)) return 0;
            if (!SequencedLogFormat.TryDecode(lastLine, out var lastEntry)) return 0;
            return lastEntry.Sequence + 1;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to determine starting sequence for log at {Path}; restarting at 0", path);
            return 0;
        }
    }

    private static string ReadLastLine(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        string? last = null;
        string? line;
        while ((line = reader.ReadLine()) != null)
            if (line.Length > 0) last = line;
        return last ?? string.Empty;
    }
}

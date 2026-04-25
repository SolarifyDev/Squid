using System.Text;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution.Masking;
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
    private readonly SensitiveValueMasker _masker;
    private readonly object _sync = new();
    private long _nextSequence;
    private int _disposed;

    public SequencedLogWriter(string logFilePath) : this(logFilePath, masker: null) { }

    public SequencedLogWriter(string logFilePath, SensitiveValueMasker masker)
    {
        if (string.IsNullOrWhiteSpace(logFilePath))
            throw new ArgumentException("Log file path required", nameof(logFilePath));

        var directory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _nextSequence = DetermineStartSequence(logFilePath);
        _stream = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, FileOptions.WriteThrough);
        _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _masker = masker;
    }

    public long NextSequence
    {
        get { lock (_sync) return _nextSequence; }
    }

    public long Append(ProcessOutputSource source, string text, DateTimeOffset? occurred = null)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));

        var masked = _masker != null ? _masker.Mask(text) : text;

        lock (_sync)
        {
            var seq = _nextSequence++;
            var entry = new SequencedLogEntry(seq, occurred ?? DateTimeOffset.UtcNow, source, masked);
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

    /// <summary>
    /// Determines the next sequence number for an existing on-disk log so a
    /// freshly-opened writer continues the existing stream instead of restarting
    /// at 0.
    ///
    /// <para><b>P1-T.12 (Phase-5 follow-up to 2026-04-24 audit)</b>: pre-fix the
    /// implementation read ONLY the last non-empty line and decoded it. If
    /// that line was a truncated/corrupt mid-crash tail (the SequencedLogReader
    /// already tolerates these via <c>Crash_TruncatedFinalLine_IgnoredByReader</c>),
    /// <c>TryDecode</c> failed → fallback to sequence 0 → next <c>Append</c>
    /// reused sequences 0..N-1 that the reader could still decode from disk.
    /// Server's cursor-tracked log delivery (Phase-4 fix) then saw duplicate
    /// sequence numbers, mis-ordered lines, and silent re-deliveries.</para>
    ///
    /// <para>Fix: scan the whole file for the highest decodable sequence —
    /// same forgiving logic the reader already uses. Garbage lines anywhere
    /// in the file are tolerated; restart point is exactly one past the
    /// highest valid entry. If no entries decode at all, restart at 0
    /// (treats the file as fresh, matching the no-file case).</para>
    ///
    /// <para>Pinned by <c>SequencedLogTests.NewWriterOnFileWithCorruptTail_*</c>.</para>
    /// </summary>
    private static long DetermineStartSequence(string path)
    {
        if (!File.Exists(path)) return 0;

        try
        {
            var reader = new SequencedLogReader(path);
            var highest = reader.GetHighestSequence();

            // GetHighestSequence returns -1 when the file has no decodable
            // entries (empty file or all garbage). -1 + 1 == 0, which is the
            // "fresh stream" starting sequence — matches the no-file case.
            return highest + 1;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Failed to determine starting sequence for log at {Path}; restarting at 0", path);
            return 0;
        }
    }
}

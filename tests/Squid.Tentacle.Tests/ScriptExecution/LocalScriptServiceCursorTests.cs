using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.Logging;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Pins the contract of <see cref="LocalScriptService.ReadLogsAndCursor(string, long)"/>:
/// the returned <c>NextSequence</c> cursor is ALWAYS one past the highest
/// sequence in the entries actually returned, OR (when the read is empty)
/// equal to <c>afterSequence + 1</c>. Never derived from a separate state
/// source that could race ahead of the disk snapshot.
///
/// <para><b>The race this prevents</b> (regression for CI failure on
/// <c>LogCursor_SurvivesAgentRestart_AllLinesDeliveredExactlyOnce</c>):</para>
///
/// <para>The previous <c>BuildStatus</c> read the writer's in-memory
/// <c>NextSequence</c> counter as the cursor, separately from the disk
/// <c>ReadFrom</c>. A concurrent writer between the two reads (e.g. while
/// bash is still emitting the final lines of an echo loop) bumped the counter
/// past entries that the disk read didn't see — so the returned cursor jumped
/// past undelivered entries. The next poll asked "from cursor onward" and
/// silently skipped the gap. CI saw <c>line-1..line-5, line-8..line-10</c>
/// (lines 6 and 7 lost). The same bug existed in
/// <c>TryBuildStatusFromPersistedLogs</c> via two separate disk reads
/// (<c>ReadFrom</c> + <c>GetHighestSequence</c>).</para>
///
/// <para>Helper-level tests here are deterministic — they verify the cursor
/// arithmetic against directly-controlled disk state. The end-to-end
/// regression test (script-emitted lines under cursor-tracked polling) is
/// pinned by <c>LocalScriptServiceIdempotencyTests.LogCursor_SurvivesAgentRestart_*</c>.</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class LocalScriptServiceCursorTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-cursor-{Guid.NewGuid():N}");
    private readonly string _logPath;

    public LocalScriptServiceCursorTests()
    {
        Directory.CreateDirectory(_workspace);
        _logPath = Path.Combine(_workspace, "output.log");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── Empty-disk and out-of-range cursors ───────────────────────────────────

    [Fact]
    public void ReadLogsAndCursor_NoFile_ReturnsEmpty_CursorAtAfterSequencePlusOne()
    {
        // Initial poll on a fresh workspace before any output has been written:
        // caller passes afterSequence=-1 (i.e. LastLogSequence=0). Cursor must
        // come back as 0 so the next poll re-tries from the beginning.
        var (logs, nextSequence) = LocalScriptService.ReadLogsAndCursor(_logPath, afterSequence: -1);

        logs.ShouldBeEmpty();
        nextSequence.ShouldBe(0);
    }

    [Fact]
    public void ReadLogsAndCursor_CursorPastEnd_ReturnsEmpty_CursorPreservesProgress()
    {
        // Caller has already received everything on disk. Cursor must NOT
        // rewind to disk-highest+1 (that would re-deliver) — it stays one
        // past whatever the caller said it has.
        using (var writer = new SequencedLogWriter(_logPath))
        {
            for (var i = 0; i < 3; i++) writer.Append(ProcessOutputSource.StdOut, $"line-{i}");
        }

        var (logs, nextSequence) = LocalScriptService.ReadLogsAndCursor(_logPath, afterSequence: 99);

        logs.ShouldBeEmpty();
        nextSequence.ShouldBe(100,
            customMessage: "cursor past disk content must not rewind — preserves caller progress.");
    }

    // ── Single-pass reads — cursor exactly matches returned tail ──────────────

    [Fact]
    public void ReadLogsAndCursor_FromBeginning_ReturnsAll_CursorOnePastHighest()
    {
        using (var writer = new SequencedLogWriter(_logPath))
        {
            for (var i = 0; i < 5; i++) writer.Append(ProcessOutputSource.StdOut, $"line-{i}");
        }

        var (logs, nextSequence) = LocalScriptService.ReadLogsAndCursor(_logPath, afterSequence: -1);

        logs.Count.ShouldBe(5);
        logs.Select(l => l.Text).ShouldBe(new[] { "line-0", "line-1", "line-2", "line-3", "line-4" });
        nextSequence.ShouldBe(5,
            customMessage: "cursor must be exactly one past the highest sequence returned (4 + 1 = 5).");
    }

    [Fact]
    public void ReadLogsAndCursor_MidStreamCursor_ReturnsTail_CursorOnePastHighest()
    {
        using (var writer = new SequencedLogWriter(_logPath))
        {
            for (var i = 0; i < 10; i++) writer.Append(ProcessOutputSource.StdOut, $"line-{i}");
        }

        var (logs, nextSequence) = LocalScriptService.ReadLogsAndCursor(_logPath, afterSequence: 4);

        logs.Count.ShouldBe(5);
        logs.Select(l => l.Text).ShouldBe(new[] { "line-5", "line-6", "line-7", "line-8", "line-9" });
        nextSequence.ShouldBe(10,
            customMessage: "after delivering 5..9, cursor advances to 10 (highest seq + 1).");
    }

    // ── Two-phase delivery — no gap, no overlap across cursor advance ─────────

    [Fact]
    public void ReadLogsAndCursor_TwoSequentialReads_NoGapNoOverlap()
    {
        // Property the original BUG violated: between two reads, every entry
        // must be delivered exactly once when the caller threads the cursor.
        // Before the fix, BuildStatus's cursor could jump past entries that
        // weren't in the first read's logs (because writer.NextSequence raced
        // ahead of ReadFrom), so the second read missed them.
        using var writer = new SequencedLogWriter(_logPath);

        for (var i = 0; i < 5; i++) writer.Append(ProcessOutputSource.StdOut, $"line-{i}");
        var (firstLogs, firstCursor) = LocalScriptService.ReadLogsAndCursor(_logPath, afterSequence: -1);

        // More entries arrive — server polls again with the cursor it received.
        for (var i = 5; i < 10; i++) writer.Append(ProcessOutputSource.StdOut, $"line-{i}");
        var (secondLogs, secondCursor) = LocalScriptService.ReadLogsAndCursor(_logPath, afterSequence: firstCursor - 1);

        firstLogs.Count.ShouldBe(5);
        firstCursor.ShouldBe(5);

        secondLogs.Count.ShouldBe(5);
        secondCursor.ShouldBe(10);

        // Combined: every line exactly once, in order, no gaps.
        var combined = firstLogs.Concat(secondLogs).Select(l => l.Text).ToList();
        combined.Count.ShouldBe(10);
        for (var i = 0; i < 10; i++)
            combined.Count(t => t == $"line-{i}").ShouldBe(1, $"line-{i} must appear exactly once across the cursor advance.");
    }

    [Fact]
    public void ReadLogsAndCursor_RepeatedReadsWithSameCursor_AreIdempotent()
    {
        // If the caller hasn't advanced their cursor yet (e.g. a transient
        // network glitch retrying the same poll), each read must return the
        // same entries — never half a line, never duplicates.
        using (var writer = new SequencedLogWriter(_logPath))
        {
            for (var i = 0; i < 3; i++) writer.Append(ProcessOutputSource.StdOut, $"line-{i}");
        }

        var (a, ac) = LocalScriptService.ReadLogsAndCursor(_logPath, afterSequence: -1);
        var (b, bc) = LocalScriptService.ReadLogsAndCursor(_logPath, afterSequence: -1);
        var (c, cc) = LocalScriptService.ReadLogsAndCursor(_logPath, afterSequence: -1);

        ac.ShouldBe(3);
        bc.ShouldBe(ac);
        cc.ShouldBe(ac);

        a.Select(l => l.Text).ShouldBe(b.Select(l => l.Text));
        a.Select(l => l.Text).ShouldBe(c.Select(l => l.Text));
    }

    // ── Concurrent-writer scenario — the actual race the bug exposed ─────────

    [Fact]
    public async Task ReadLogsAndCursor_WithConcurrentWriter_NoLineLostAcrossPolls()
    {
        // The original BuildStatus race: ReadLogs (disk) + LogWriter.NextSequence
        // (in-memory counter) read separately, with appends landing between them.
        // Returned cursor jumped past entries not in returned logs → next poll
        // skipped them. With the new single-source helper, the cursor is derived
        // from the entries actually returned, so it is structurally impossible
        // for it to overshoot — even under heavy concurrent writes.
        using var writer = new SequencedLogWriter(_logPath);

        const int totalLines = 200;
        var writerDone = new TaskCompletionSource<bool>();

        var bgWriter = Task.Run(() =>
        {
            for (var i = 0; i < totalLines; i++)
                writer.Append(ProcessOutputSource.StdOut, $"line-{i}");
            writerDone.SetResult(true);
        });

        // Poll loop — emulates the server's cursor-tracked observer.
        long cursor = 0;
        var collected = new List<string>();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var (logs, nextSequence) = LocalScriptService.ReadLogsAndCursor(_logPath, afterSequence: cursor - 1);
            collected.AddRange(logs.Select(l => l.Text));
            cursor = nextSequence;

            if (writerDone.Task.IsCompleted && cursor >= totalLines) break;
            await Task.Delay(2);
        }

        await bgWriter;

        // Final drain.
        var (final, finalCursor) = LocalScriptService.ReadLogsAndCursor(_logPath, afterSequence: cursor - 1);
        collected.AddRange(final.Select(l => l.Text));

        finalCursor.ShouldBe(totalLines);
        collected.Count.ShouldBe(totalLines,
            customMessage: $"polled cursor must deliver every line exactly once; got {collected.Count} of {totalLines}.");

        // No duplicates and no gaps.
        for (var i = 0; i < totalLines; i++)
            collected.Count(t => t == $"line-{i}").ShouldBe(1, $"line-{i} must be delivered exactly once across cursor-tracked polls.");
    }
}

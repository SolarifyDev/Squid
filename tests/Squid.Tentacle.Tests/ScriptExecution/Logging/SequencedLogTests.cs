using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution.Logging;
using Squid.Tentacle.ScriptExecution.Masking;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution.Logging;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class SequencedLogTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-log-test-{Guid.NewGuid():N}");
    private readonly string _logPath;

    public SequencedLogTests()
    {
        Directory.CreateDirectory(_workspace);
        _logPath = Path.Combine(_workspace, "output.log");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    // ========================================================================
    // Writer — sequence numbers strictly increasing, disk-flushed
    // ========================================================================

    [Fact]
    public void Append_AssignsStrictlyIncreasingSequences()
    {
        using var writer = new SequencedLogWriter(_logPath);

        var s1 = writer.Append(ProcessOutputSource.StdOut, "first");
        var s2 = writer.Append(ProcessOutputSource.StdOut, "second");
        var s3 = writer.Append(ProcessOutputSource.StdErr, "third");

        s1.ShouldBe(0);
        s2.ShouldBe(1);
        s3.ShouldBe(2);
        writer.NextSequence.ShouldBe(3);
    }

    [Fact]
    public void Append_ConcurrentProducers_NoSequenceGapsOrDuplicates()
    {
        using var writer = new SequencedLogWriter(_logPath);

        Parallel.For(0, 200, i =>
        {
            var src = i % 2 == 0 ? ProcessOutputSource.StdOut : ProcessOutputSource.StdErr;
            writer.Append(src, $"line-{i}");
        });

        var reader = new SequencedLogReader(_logPath);
        var entries = reader.ReadFrom(afterSequence: -1);

        entries.Count.ShouldBe(200);
        entries.Select(e => e.Sequence).ShouldBe(Enumerable.Range(0, 200).Select(i => (long)i));
    }

    [Fact]
    public void NewWriterOnExistingFile_ContinuesSequence()
    {
        using (var first = new SequencedLogWriter(_logPath))
        {
            first.Append(ProcessOutputSource.StdOut, "a");
            first.Append(ProcessOutputSource.StdOut, "b");
            first.Append(ProcessOutputSource.StdOut, "c");
        }

        // Simulate agent restart: reopen the log file with a fresh writer instance.
        using var second = new SequencedLogWriter(_logPath);
        second.NextSequence.ShouldBe(3);

        var s = second.Append(ProcessOutputSource.StdOut, "d");
        s.ShouldBe(3);
    }

    // ========================================================================
    // Reader — cursor-based, safe in face of truncation
    // ========================================================================

    [Fact]
    public void ReadFrom_Cursor_ReturnsOnlyNewEntries()
    {
        using var writer = new SequencedLogWriter(_logPath);
        for (var i = 0; i < 10; i++)
            writer.Append(ProcessOutputSource.StdOut, $"line-{i}");

        var reader = new SequencedLogReader(_logPath);
        var after5 = reader.ReadFrom(afterSequence: 5);

        after5.Count.ShouldBe(4);                            // sequences 6,7,8,9
        after5.Select(e => e.Sequence).ShouldBe(new long[] { 6, 7, 8, 9 });
        after5.Select(e => e.Text).ShouldBe(new[] { "line-6", "line-7", "line-8", "line-9" });
    }

    [Fact]
    public void ReadFrom_Cursor_BeyondLatest_ReturnsEmpty()
    {
        using var writer = new SequencedLogWriter(_logPath);
        writer.Append(ProcessOutputSource.StdOut, "only one");

        var reader = new SequencedLogReader(_logPath);

        reader.ReadFrom(afterSequence: 999).ShouldBeEmpty();
    }

    [Fact]
    public void ReadFrom_NoFile_ReturnsEmpty()
    {
        var reader = new SequencedLogReader(Path.Combine(_workspace, "does-not-exist.log"));

        reader.ReadFrom(afterSequence: -1).ShouldBeEmpty();
    }

    [Fact]
    public void Crash_TruncatedFinalLine_IgnoredByReader()
    {
        using (var writer = new SequencedLogWriter(_logPath))
        {
            writer.Append(ProcessOutputSource.StdOut, "clean-line-1");
            writer.Append(ProcessOutputSource.StdOut, "clean-line-2");
        }

        // Simulate a crashed write that left a partial line with no trailing newline.
        File.AppendAllText(_logPath, "000000000002\tnot-a-date\tO\tnot-valid-base64-!");

        var reader = new SequencedLogReader(_logPath);
        var entries = reader.ReadFrom(afterSequence: -1);

        // The two good lines survive, the bad tail is silently dropped.
        entries.Count.ShouldBe(2);
        entries[0].Text.ShouldBe("clean-line-1");
        entries[1].Text.ShouldBe("clean-line-2");
    }

    [Fact]
    public void PayloadContainingTabAndNewline_IsSafelyRoundTripped()
    {
        using var writer = new SequencedLogWriter(_logPath);
        writer.Append(ProcessOutputSource.StdOut, "field-a\tfield-b\nline-2");

        var reader = new SequencedLogReader(_logPath);
        var entries = reader.ReadFrom(afterSequence: -1);

        entries.ShouldHaveSingleItem();
        entries[0].Text.ShouldBe("field-a\tfield-b\nline-2");
    }

    [Fact]
    public void UnicodeText_SurvivesRoundTrip()
    {
        using var writer = new SequencedLogWriter(_logPath);
        writer.Append(ProcessOutputSource.StdOut, "你好 — deployment 部署 🚀");

        var reader = new SequencedLogReader(_logPath);
        var entries = reader.ReadFrom(afterSequence: -1);

        entries.ShouldHaveSingleItem();
        entries[0].Text.ShouldBe("你好 — deployment 部署 🚀");
    }

    // ========================================================================
    // Integration scenario — the exact "agent restart" behaviour we want
    // ========================================================================

    [Fact]
    public void Writer_WithMasker_WritesMaskedPayload_SensitiveValueNeverOnDisk()
    {
        var masker = new SensitiveValueMasker(new[] { "super-secret-pw-123" });
        using (var writer = new SequencedLogWriter(_logPath, masker))
        {
            writer.Append(ProcessOutputSource.StdOut, "connecting with super-secret-pw-123 to db");
            writer.Append(ProcessOutputSource.StdErr, "failure: super-secret-pw-123 rejected");
        }

        // The on-disk log must never contain the raw secret — end-to-end proof
        // that the masker is applied before fsync, not only at read time.
        var raw = File.ReadAllText(_logPath);
        raw.ShouldNotContain("super-secret-pw-123");

        // Reader returns masked entries.
        var reader = new SequencedLogReader(_logPath);
        var entries = reader.ReadFrom(afterSequence: -1);
        entries.ShouldAllBe(e => e.Text.Contains("***"));
        entries.ShouldAllBe(e => !e.Text.Contains("super-secret-pw-123"));
    }

    [Fact]
    public void AgentRestartScenario_LogsBeforeCrashArePreserved_ServerSeesThemOnResume()
    {
        // 1. Script runs, agent writes 10 lines, server polled once and got first 5.
        long serverCursor;
        using (var writer = new SequencedLogWriter(_logPath))
        {
            for (var i = 0; i < 5; i++)
                writer.Append(ProcessOutputSource.StdOut, $"line-{i}");

            var reader = new SequencedLogReader(_logPath);
            var firstBatch = reader.ReadFrom(afterSequence: -1);
            serverCursor = firstBatch.Max(e => e.Sequence);

            for (var i = 5; i < 10; i++)
                writer.Append(ProcessOutputSource.StdOut, $"line-{i}");
        }

        // 2. Agent crashes. New agent instance opens the log file.
        //    Server polls again with its last cursor — must get lines 5-9 exactly once.
        var resumedReader = new SequencedLogReader(_logPath);
        var secondBatch = resumedReader.ReadFrom(afterSequence: serverCursor);

        secondBatch.Count.ShouldBe(5);
        secondBatch.Select(e => e.Text).ShouldBe(new[] { "line-5", "line-6", "line-7", "line-8", "line-9" });
    }
}

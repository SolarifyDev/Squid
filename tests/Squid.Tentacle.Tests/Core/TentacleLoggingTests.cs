using Squid.Tentacle.Core;
using Squid.Tentacle.Platform;

namespace Squid.Tentacle.Tests.Core;

/// <summary>
/// Pins the tentacle's logging contract (audit P2): a persistent, size-rotated
/// FILE sink is added ONLY when running as the managed service (Windows SCM,
/// where the console goes to NUL); short-lived CLI commands stay console-only.
/// Also pins the single-generation rotation of the separate append-only
/// SCM-startup diagnostic log.
/// </summary>
public sealed class TentacleLoggingTests : IDisposable
{
    private readonly string _tempDir;

    public TentacleLoggingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "squid-tentacle-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // ── Operator-visible constants pinned ────────────────────────────────────

    [Fact]
    public void Defaults_Pinned()
    {
        TentacleLogging.FileSizeLimitBytes.ShouldBe(50L * 1024 * 1024);
        TentacleLogging.RetainedFileCountLimit.ShouldBe(5);
        TentacleLogging.AgentLogFileName.ShouldBe("tentacle.log");
        TentacleLogging.LogsDirName.ShouldBe("logs");
        TentacleLogging.ScmDiagnosticMaxBytes.ShouldBe(1024 * 1024);
    }

    [Fact]
    public void ResolveAgentLogPath_IsUnderSystemConfigDir_InLogsSubdir()
    {
        var path = TentacleLogging.ResolveAgentLogPath();

        path.ShouldStartWith(PlatformPaths.GetSystemConfigDir());
        path.ShouldEndWith(Path.Combine("logs", "tentacle.log"));
    }

    // ── File-sink gating ─────────────────────────────────────────────────────

    [Fact]
    public void NoFileSink_WritesNoFile()
    {
        // Short-lived CLI command path: console-only, must not litter a log file.
        var logPath = Path.Combine(_tempDir, "should-not-appear.log");

        var logger = TentacleLogging.BuildLoggerConfiguration(addPersistentFileSink: false, logPathOverride: logPath).CreateLogger();
        logger.Information("a short-lived CLI command line");
        logger.Dispose();

        File.Exists(logPath).ShouldBeFalse();
        Directory.GetFiles(_tempDir).ShouldBeEmpty();
    }

    [Fact]
    public void WithFileSink_WritesPersistentLog()
    {
        // Managed-service path: the runtime log MUST be persisted to disk.
        var logPath = Path.Combine(_tempDir, "tentacle.log");

        var logger = TentacleLogging.BuildLoggerConfiguration(addPersistentFileSink: true, logPathOverride: logPath).CreateLogger();
        logger.Information("agent runtime line {N}", 42);
        logger.Dispose();

        File.Exists(logPath).ShouldBeTrue();
        File.ReadAllText(logPath).ShouldContain("agent runtime line 42");
    }

    [Fact]
    public void WithFileSink_RotatesWhenSizeLimitExceeded()
    {
        // Proves the rolling sink actually rotates (the "no rotation" gap). Tiny
        // size override so a handful of lines roll the file without writing 50 MB.
        var logPath = Path.Combine(_tempDir, "tentacle.log");

        var logger = TentacleLogging.BuildLoggerConfiguration(
            addPersistentFileSink: true, logPathOverride: logPath, fileSizeLimitBytesOverride: 512).CreateLogger();

        for (var i = 0; i < 200; i++)
            logger.Information("rotation probe line {I} with padding to grow the file quickly", i);

        logger.Dispose();

        var files = Directory.GetFiles(_tempDir, "tentacle*.log");
        files.Length.ShouldBeGreaterThan(1,
            customMessage: "rollOnFileSizeLimit should have produced multiple rolled files");
        files.Length.ShouldBeLessThanOrEqualTo(TentacleLogging.RetainedFileCountLimit,
            customMessage: "retainedFileCountLimit should cap how many rolled files are kept");
    }

    [Fact]
    public void WithFileSink_LogDirCannotBeCreated_FallsBackToConsoleOnly_NeverThrows()
    {
        // The hard guarantee (TentacleLogging.TryResolveWritableLogPath): a log
        // file must NEVER block the service from starting. Force
        // Directory.CreateDirectory to throw by making the log path's parent a
        // regular FILE, then assert building the logger neither throws nor leaves
        // a file — it silently falls back to console-only.
        var blockingFile = Path.Combine(_tempDir, "not-a-dir");
        File.WriteAllText(blockingFile, "i am a file, not a directory");

        // {_tempDir}/not-a-dir/logs/tentacle.log — the "logs" dir can't be created
        // under a regular file, so TryResolveWritableLogPath catches + returns false.
        var impossiblePath = Path.Combine(blockingFile, "logs", "tentacle.log");

        Should.NotThrow(() =>
        {
            using var logger = TentacleLogging.BuildLoggerConfiguration(
                addPersistentFileSink: true, logPathOverride: impossiblePath).CreateLogger();
            logger.Information("the service must still start when the log dir can't be created");
        });

        File.Exists(impossiblePath).ShouldBeFalse(
            customMessage: "fell back to console-only — no log file should be produced at the un-creatable path");
    }

    // ── SCM-diagnostic single-generation rotation ────────────────────────────

    [Fact]
    public void RotateIfOversized_UnderLimit_LeavesFileUntouched()
    {
        var path = Path.Combine(_tempDir, "scm.log");
        File.WriteAllText(path, "small");

        TentacleLogging.RotateIfOversized(path, maxBytes: 1024);

        File.ReadAllText(path).ShouldBe("small");
        File.Exists(path + ".old").ShouldBeFalse();
    }

    [Fact]
    public void RotateIfOversized_OverLimit_MovesToOldAndTruncates()
    {
        var path = Path.Combine(_tempDir, "scm.log");
        File.WriteAllText(path, new string('x', 2048));

        TentacleLogging.RotateIfOversized(path, maxBytes: 1024);

        File.Exists(path + ".old").ShouldBeTrue();
        new FileInfo(path + ".old").Length.ShouldBe(2048);
        new FileInfo(path).Length.ShouldBe(0,
            customMessage: "active file must be truncated after rotation so it can't grow unbounded");
    }

    [Fact]
    public void RotateIfOversized_MissingFile_DoesNotThrow()
    {
        var path = Path.Combine(_tempDir, "does-not-exist.log");

        Should.NotThrow(() => TentacleLogging.RotateIfOversized(path, maxBytes: 1));
        File.Exists(path + ".old").ShouldBeFalse();
    }
}

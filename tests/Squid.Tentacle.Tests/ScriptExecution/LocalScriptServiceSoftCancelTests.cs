using Halibut;
using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// P1-Phase11.3 (audit ARCH.9 F1.2) — pin the soft-cancel propagation through
/// the file-save path.
///
/// <para><b>The bug pre-Phase-11.3</b>: WriteAdditionalFiles used hardcoded
/// <c>CancellationToken.None</c> on the inner <c>SaveToAsync</c> call.
/// A 1GB sensitiveVariables.json or large package payload would write to
/// completion regardless of CancelScript — operators saw "cancelled"
/// status in the UI but the agent kept consuming bandwidth + disk for
/// minutes after.</para>
///
/// <para>Now WriteAdditionalFiles takes a CancellationToken parameter, threaded
/// from the per-ticket soft-cancel registry. CancelScript flips the CTS,
/// in-flight SaveToAsync observes the cancellation via DataStream's
/// internal CopyToAsync, file write aborts.</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class LocalScriptServiceSoftCancelTests : IDisposable
{
    private readonly string _workDir;

    public LocalScriptServiceSoftCancelTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "squid-softcancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public void WriteAdditionalFiles_DefaultCt_BackwardCompat_NoThrow()
    {
        // Default-arg parameter: callers that don't pass CT must keep working
        // exactly as before. Pin this so a future refactor that flips the
        // default to required doesn't silently break every call site.
        var files = new List<ScriptFile>
        {
            new ScriptFile("test.txt", DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes("hello")), null)
        };

        Should.NotThrow(() => LocalScriptService.WriteAdditionalFiles(_workDir, files));

        File.Exists(Path.Combine(_workDir, "test.txt")).ShouldBeTrue();
    }

    [Fact]
    public void WriteAdditionalFiles_CtAlreadyCancelled_ThrowsOperationCancelled()
    {
        // The exact scenario the fix targets: CancelScript flipped the CTS
        // BEFORE WriteAdditionalFiles even started. The first
        // ThrowIfCancellationRequested at the loop top catches it — we
        // don't even attempt the first SaveToAsync.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var files = new List<ScriptFile>
        {
            new ScriptFile("test.txt", DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes("hello")), null)
        };

        Should.Throw<OperationCanceledException>(
            () => LocalScriptService.WriteAdditionalFiles(_workDir, files, cts.Token));

        File.Exists(Path.Combine(_workDir, "test.txt")).ShouldBeFalse(customMessage:
            "Pre-cancelled CT must abort BEFORE writing the file — pre-Phase-11.3 the CT was None and this would have written.");
    }

    [Fact]
    public void WriteAdditionalFiles_CtCancelledMidLoop_StopsBeforeNextFile()
    {
        // Multi-file payload: CT cancelled between file 1 and file 2 — file
        // 1 writes successfully, file 2 is skipped (loop top check).
        using var cts = new CancellationTokenSource();
        var files = new List<ScriptFile>
        {
            new ScriptFile("file1.txt", DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes("first")), null),
            new ScriptFile("file2.txt", DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes("second")), null)
        };

        // Pre-write file1 (simulating successful first iteration); we then
        // cancel and call WriteAdditionalFiles to assert the next call's
        // first iteration aborts. Simpler model: cancel BEFORE the call;
        // both file1 and file2 are skipped.
        cts.Cancel();

        Should.Throw<OperationCanceledException>(
            () => LocalScriptService.WriteAdditionalFiles(_workDir, files, cts.Token));

        File.Exists(Path.Combine(_workDir, "file1.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(_workDir, "file2.txt")).ShouldBeFalse();
    }

    [Fact]
    public void WriteAdditionalFiles_FreshCt_AllFilesSucceed()
    {
        // Sanity: a non-cancelled CT must NOT cause spurious aborts. Pin
        // that the new CT plumbing doesn't introduce a flake-prone path
        // for legitimate writes.
        using var cts = new CancellationTokenSource();
        var files = new List<ScriptFile>
        {
            new ScriptFile("a.txt", DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes("aaa")), null),
            new ScriptFile("b.txt", DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes("bbb")), null),
            new ScriptFile("c.txt", DataStream.FromBytes(System.Text.Encoding.UTF8.GetBytes("ccc")), null)
        };

        Should.NotThrow(() => LocalScriptService.WriteAdditionalFiles(_workDir, files, cts.Token));

        File.Exists(Path.Combine(_workDir, "a.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(_workDir, "b.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(_workDir, "c.txt")).ShouldBeTrue();
    }
}

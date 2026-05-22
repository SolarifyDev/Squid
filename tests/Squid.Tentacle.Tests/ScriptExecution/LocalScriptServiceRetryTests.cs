using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Squid.Tentacle.ScriptExecution;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Tests for <see cref="LocalScriptService.RetryOnTransientIO"/> — the retry +
/// backoff helper that wraps Halibut <c>SaveToAsync</c> calls so transient
/// Windows Defender / AV file-lock IOExceptions don't crash an entire
/// StartScript RPC.
///
/// <para><b>Real production failure mode pinned by these tests</b>:</para>
/// <code>
///   IOException: The process cannot access the file 'C:\Windows\TEMP\{guid}_1'
///   because it is being used by another process.
///     at Halibut.Transport.Protocol.TemporaryFileStream.SaveToAsync(string filePath, ...)
///     at Squid.Tentacle.ScriptExecution.LocalScriptService.WriteAdditionalFiles(...)
/// </code>
///
/// <para>Tests inject a synthetic <see cref="Action"/> that throws IOException
/// for the first N attempts then succeeds (or never succeeds). The retry
/// contract — attempt count, backoff timing, IOException-only catch,
/// cancellation between attempts, env-var override — is fully exercised
/// without involving Halibut's DataStream wire types.</para>
/// </summary>
public sealed class LocalScriptServiceRetryTests
{
    // ── Happy / sad paths ────────────────────────────────────────────────────

    [Fact]
    public void RetryOnTransientIO_SuccessOnFirstAttempt_NoRetry()
    {
        var calls = 0;

        LocalScriptService.RetryOnTransientIO(
            operation: () => calls++,
            contextName: "test.txt",
            cancellationToken: CancellationToken.None);

        calls.ShouldBe(1, customMessage: "no exception means no retry");
    }

    [Theory]
    [InlineData(1)]    // succeeds on 2nd attempt
    [InlineData(2)]    // succeeds on 3rd
    [InlineData(3)]    // succeeds on 4th
    [InlineData(4)]    // succeeds on 5th (last)
    public void RetryOnTransientIO_TransientIOException_RetriesUntilSuccess(int failuresBeforeSuccess)
    {
        var calls = 0;

        LocalScriptService.RetryOnTransientIO(
            operation: () =>
            {
                calls++;
                if (calls <= failuresBeforeSuccess)
                    throw new IOException(
                        $"The process cannot access the file 'C:\\Windows\\TEMP\\{Guid.NewGuid():N}_1' because it is being used by another process.");
            },
            contextName: "package.nupkg",
            cancellationToken: CancellationToken.None);

        calls.ShouldBe(failuresBeforeSuccess + 1,
            customMessage: "operation must be retried until success");
    }

    [Fact]
    public void RetryOnTransientIO_AllAttemptsFail_ThrowsWrappedIOExceptionWithAttemptCount()
    {
        var calls = 0;
        var originalMessage = "The process cannot access the file 'C:\\Windows\\TEMP\\victim' because it is being used by another process.";

        var ex = Should.Throw<IOException>(() => LocalScriptService.RetryOnTransientIO(
            operation: () =>
            {
                calls++;
                throw new IOException(originalMessage);
            },
            contextName: "stubborn.bin",
            cancellationToken: CancellationToken.None));

        calls.ShouldBe(LocalScriptService.SaveDataStreamMaxAttempts,
            customMessage: $"every attempt up to the max ({LocalScriptService.SaveDataStreamMaxAttempts}) must fire");

        ex.Message.ShouldContain("stubborn.bin",
            customMessage: "wrapper message must include the context name so operators can grep logs by filename");

        ex.Message.ShouldContain($"after {LocalScriptService.SaveDataStreamMaxAttempts} attempts",
            customMessage: "wrapper message must surface the attempt count");

        ex.Message.ShouldContain(LocalScriptService.SaveDataStreamMaxAttemptsEnvVar,
            customMessage: "wrapper message must name the env-var escape hatch (Rule 8)");

        ex.InnerException.ShouldNotBeNull(customMessage: "the original IOException must be preserved as InnerException");
        ex.InnerException!.Message.ShouldBe(originalMessage,
            customMessage: "InnerException carries the ORIGINAL message so root-cause stays visible");
    }

    [Fact]
    public void RetryOnTransientIO_NonIOException_ThrowsImmediatelyWithoutRetry()
    {
        // Only IOException is the retry trigger. Any other exception must
        // propagate immediately so genuine bugs aren't masked by 5 seconds of
        // retry sleep.
        var calls = 0;

        Should.Throw<InvalidOperationException>(() => LocalScriptService.RetryOnTransientIO(
            operation: () =>
            {
                calls++;
                throw new InvalidOperationException("genuine bug, not transient");
            },
            contextName: "test.txt",
            cancellationToken: CancellationToken.None));

        calls.ShouldBe(1,
            customMessage: "non-IOException MUST propagate without retry — only the transient AV lock case retries");
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public void RetryOnTransientIO_CancellationBeforeFirstAttempt_ThrowsImmediately()
    {
        var calls = 0;
        var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(() => LocalScriptService.RetryOnTransientIO(
            operation: () => calls++,
            contextName: "test.txt",
            cancellationToken: cts.Token));

        calls.ShouldBe(0, customMessage: "pre-cancelled token MUST short-circuit before any operation runs");
    }

    [Fact]
    public void RetryOnTransientIO_CancellationDuringBackoff_AbortsRetryLoop()
    {
        // Operator hit CancelScript mid-retry. The Task.Delay between attempts
        // must observe the token and unwind so the cancel response is fast
        // (not blocked behind the full ~2.5s retry budget).
        var calls = 0;
        var cts = new CancellationTokenSource();

        Should.Throw<OperationCanceledException>(() => LocalScriptService.RetryOnTransientIO(
            operation: () =>
            {
                calls++;
                if (calls == 1) cts.Cancel();    // cancel between attempt 1 and 2
                throw new IOException("transient");
            },
            contextName: "cancelled-mid-retry.bin",
            cancellationToken: cts.Token));

        calls.ShouldBe(1,
            customMessage: "cancel between attempts MUST stop the loop before the next operation runs");
    }

    // ── Env-var override (Rule 8) ────────────────────────────────────────────

    [Fact]
    public void SaveDataStreamMaxAttemptsEnvVar_LiteralPinned()
    {
        // Air-gapped operators with aggressive AV may need to raise the
        // attempt count. Pin the env var name so a silent rename doesn't
        // strand their override in production.
        LocalScriptService.SaveDataStreamMaxAttemptsEnvVar
            .ShouldBe("SQUID_TENTACLE_SAVE_DATASTREAM_MAX_ATTEMPTS");
    }

    [Fact]
    public void RetryOnTransientIO_EnvVarRaisesAttemptCount()
    {
        var calls = 0;

        Environment.SetEnvironmentVariable(
            LocalScriptService.SaveDataStreamMaxAttemptsEnvVar, "8");

        try
        {
            Should.Throw<IOException>(() => LocalScriptService.RetryOnTransientIO(
                operation: () =>
                {
                    calls++;
                    throw new IOException("always");
                },
                contextName: "test.txt",
                cancellationToken: CancellationToken.None));

            calls.ShouldBe(8,
                customMessage: "env var override MUST replace the default attempt count");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                LocalScriptService.SaveDataStreamMaxAttemptsEnvVar, null);
        }
    }

    [Theory]
    [InlineData("0")]         // zero → fall back to default
    [InlineData("-1")]        // negative → fall back to default
    [InlineData("not-int")]   // garbage → fall back to default
    [InlineData("  ")]        // whitespace → fall back to default
    public void RetryOnTransientIO_EnvVarInvalidValue_FallsBackToDefault(string envValue)
    {
        var calls = 0;

        Environment.SetEnvironmentVariable(
            LocalScriptService.SaveDataStreamMaxAttemptsEnvVar, envValue);

        try
        {
            Should.Throw<IOException>(() => LocalScriptService.RetryOnTransientIO(
                operation: () =>
                {
                    calls++;
                    throw new IOException("always");
                },
                contextName: "test.txt",
                cancellationToken: CancellationToken.None));

            calls.ShouldBe(LocalScriptService.SaveDataStreamMaxAttempts,
                customMessage: $"invalid env var '{envValue}' MUST fall back to the default ({LocalScriptService.SaveDataStreamMaxAttempts})");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                LocalScriptService.SaveDataStreamMaxAttemptsEnvVar, null);
        }
    }

    // ── Backoff timing (sanity check) ────────────────────────────────────────

    [Fact]
    public async Task RetryOnTransientIO_AppliesBackoff_BetweenAttempts()
    {
        // Don't pin exact timings (CI noise), but verify the retry loop actually
        // sleeps between attempts. With InitialDelay=100ms and 3 failures+success,
        // total wait MUST be at least 100+200 = 300ms.
        var calls = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Task.Run(() =>
            LocalScriptService.RetryOnTransientIO(
                operation: () =>
                {
                    calls++;
                    if (calls <= 2) throw new IOException("transient");
                },
                contextName: "timing.txt",
                cancellationToken: CancellationToken.None));

        sw.Stop();

        calls.ShouldBe(3, customMessage: "should succeed on 3rd attempt");
        sw.ElapsedMilliseconds.ShouldBeGreaterThan(250,
            customMessage: "backoff between attempts MUST insert real sleep — 2 failures + success = at least " +
                $"{LocalScriptService.SaveDataStreamInitialDelayMs} + {LocalScriptService.SaveDataStreamInitialDelayMs * 2} = " +
                $"{LocalScriptService.SaveDataStreamInitialDelayMs + LocalScriptService.SaveDataStreamInitialDelayMs * 2}ms");
    }
}

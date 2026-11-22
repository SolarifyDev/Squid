using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Halibut.Exceptions;
using Shouldly;
using Squid.Core.Halibut.Resilience;
using Xunit;

namespace Squid.UnitTests.Halibut.Resilience;

/// <summary>
/// Pins the per-RPC retry loop: budget, backoff, what is (not) retried, and that an exhausted budget
/// rethrows the very exception the agent call threw, so the runner's transient-pause classification is
/// unchanged. Time is fake: the injected delay advances the injected clock.
/// </summary>
public sealed class RpcRetryHandlerTests
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly List<TimeSpan> _delays = new();

    private RpcRetryHandler Handler(int statusSeconds = 120, int completeSeconds = 30, int cancelSeconds = 20)
        => new(TimeSpan.FromSeconds(statusSeconds), TimeSpan.FromSeconds(completeSeconds), TimeSpan.FromSeconds(cancelSeconds), () => _now, FakeDelay);

    private Task FakeDelay(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _delays.Add(delay);
        _now += delay;
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TransientThenSuccess_ReturnsResult_AfterOneBackoff()
    {
        var calls = 0;

        var result = await Handler().ExecuteAsync(() => ++calls == 1 ? throw new HalibutClientException("reset") : Task.FromResult(42), "GetStatus", TimeSpan.FromSeconds(120), CancellationToken.None);

        result.ShouldBe(42);
        calls.ShouldBe(2);
        _delays.ShouldBe(new[] { TimeSpan.FromMilliseconds(500) });
    }

    [Fact]
    public async Task BudgetExhausted_RethrowsTheOriginalInstance_WithinBudget_WithExponentialBackoff()
    {
        var thrown = new List<HalibutClientException>();
        var calls = 0;

        Task<int> Call()
        {
            calls++;
            var ex = new HalibutClientException($"drop {calls}");
            thrown.Add(ex);
            throw ex;
        }

        var caught = await Should.ThrowAsync<HalibutClientException>(() => Handler().ExecuteAsync(Call, "GetStatus", TimeSpan.FromSeconds(5), CancellationToken.None));

        caught.ShouldBeSameAs(thrown[^1], "the exception from the agent call is rethrown as-is, never wrapped");
        _delays.ShouldBe(new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1500) });
        _delays.Sum(d => d.TotalSeconds).ShouldBeLessThanOrEqualTo(5, "total waiting never exceeds the budget");
        calls.ShouldBe(5);
    }

    [Fact]
    public async Task BackoffIsCapped()
    {
        var calls = 0;

        await Should.ThrowAsync<HalibutClientException>(() => Handler().ExecuteAsync<int>(() => { calls++; throw new HalibutClientException("x"); }, "GetStatus", TimeSpan.FromMinutes(5), CancellationToken.None));

        _delays.ShouldAllBe(d => d <= TimeSpan.FromSeconds(10));
        _delays.ShouldContain(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ZeroBudget_DisablesRetries()
    {
        var calls = 0;

        await Should.ThrowAsync<HalibutClientException>(() => Handler().ExecuteAsync<int>(() => { calls++; throw new HalibutClientException("x"); }, "GetStatus", TimeSpan.Zero, CancellationToken.None));

        calls.ShouldBe(1);
        _delays.ShouldBeEmpty();
    }

    public static TheoryData<Exception> NonTransient() => new()
    {
        new ServiceInvocationHalibutClientException("agent service threw"),
        new NoMatchingServiceOrMethodHalibutClientException("no match"),
        new MethodNotFoundHalibutClientException("no method"),
        new CircuitOpenException(1, DateTimeOffset.UtcNow.AddMinutes(1)),
        new InvalidOperationException("bug")
    };

    [Theory]
    [MemberData(nameof(NonTransient))]
    public async Task NonTransientFailure_IsNeverRetried(Exception failure)
    {
        var calls = 0;

        var caught = await Should.ThrowAsync<Exception>(() => Handler().ExecuteAsync<int>(() => { calls++; throw failure; }, "GetStatus", TimeSpan.FromMinutes(2), CancellationToken.None));

        caught.ShouldBeSameAs(failure);
        calls.ShouldBe(1);
        _delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallerAlreadyCancelled_FailureIsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;

        var caught = await Should.ThrowAsync<HalibutClientException>(() => Handler().ExecuteAsync<int>(() => { calls++; cts.Cancel(); throw new HalibutClientException("x"); }, "GetStatus", TimeSpan.FromMinutes(2), cts.Token));

        calls.ShouldBe(1);
        _delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallerCancelsDuringBackoff_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RpcRetryHandler(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20), () => _now, (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await Should.ThrowAsync<OperationCanceledException>(() => handler.ExecuteAsync<int>(() => throw new HalibutClientException("x"), "GetStatus", TimeSpan.FromMinutes(2), cts.Token));
    }

    [Fact]
    public async Task StopRetryingFiresDuringBackoff_RethrowsLastFailure_NotCancellation()
    {
        using var stop = new CancellationTokenSource();
        var failure = new HalibutClientException("x");
        var handler = new RpcRetryHandler(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20), () => _now, (_, ct) => { stop.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        var caught = await Should.ThrowAsync<HalibutClientException>(() => handler.ExecuteAsync<int>(() => throw failure, "GetStatus", TimeSpan.FromMinutes(2), CancellationToken.None, stop.Token));

        caught.ShouldBeSameAs(failure);
    }

    [Fact]
    public void EnvVarNamesAndDefaults_ArePinned()
    {
        // Renaming either name silently drops the override an operator deployed; changing a default changes pause behaviour fleet-wide.
        RpcRetryHandler.RetryBudgetSecondsEnvVar.ShouldBe("SQUID_HALIBUT_RPC_RETRY_BUDGET_SECONDS");
        RpcRetryHandler.CompleteRetryBudgetSecondsEnvVar.ShouldBe("SQUID_HALIBUT_RPC_COMPLETE_RETRY_BUDGET_SECONDS");
        RpcRetryHandler.DefaultRetryBudgetSeconds.ShouldBe(120);
        RpcRetryHandler.DefaultCompleteRetryBudgetSeconds.ShouldBe(30);
        RpcRetryHandler.CancelRetryBudgetSecondsEnvVar.ShouldBe("SQUID_HALIBUT_RPC_CANCEL_RETRY_BUDGET_SECONDS");
        RpcRetryHandler.DefaultCancelRetryBudgetSeconds.ShouldBe(20);
    }

    [Theory]
    [InlineData(null, 120, 120)]
    [InlineData("", 120, 120)]
    [InlineData("abc", 120, 120)]
    [InlineData("-5", 120, 120)]
    [InlineData("0", 120, 0)]
    [InlineData(" 45 ", 120, 45)]
    public void ParseBudget_FallsBackToDefault_AllowsZeroToDisable(string raw, int defaultSeconds, int expectedSeconds)
        => RpcRetryHandler.ParseBudget(raw, defaultSeconds).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
}

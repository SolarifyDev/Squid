using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class TargetParallelExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_EmptyList_ReturnsEmpty()
    {
        var results = await TargetParallelExecutor.ExecuteAsync(new List<int>(), 0, (item, ct) => Task.FromResult(item), CancellationToken.None);

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_SingleItem_Runs()
    {
        var items = new List<int> { 42 };

        var results = await TargetParallelExecutor.ExecuteAsync(items, 0, (item, ct) => Task.FromResult(item * 2), CancellationToken.None);

        results.ShouldBe(new List<int> { 84 });
    }

    [Fact]
    public async Task ExecuteAsync_Sequential_WhenMaxParallelism1()
    {
        var concurrency = 0;
        var maxObserved = 0;
        var items = Enumerable.Range(1, 5).ToList();

        var results = await TargetParallelExecutor.ExecuteAsync(items, 1, async (item, ct) =>
        {
            var current = Interlocked.Increment(ref concurrency);
            Interlocked.Exchange(ref maxObserved, Math.Max(maxObserved, current));
            await Task.Delay(10, ct).ConfigureAwait(false);
            Interlocked.Decrement(ref concurrency);
            return item;
        }, CancellationToken.None);

        maxObserved.ShouldBe(1);
        results.ShouldBe(items);
    }

    [Fact]
    public async Task ExecuteAsync_AllConcurrent_WhenUnlimited()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var items = Enumerable.Range(1, 5).ToList();

        var task = TargetParallelExecutor.ExecuteAsync(items, 0, async (item, ct) =>
        {
            if (Interlocked.Increment(ref entered) >= items.Count)
                barrier.SetResult();

            await barrier.Task.ConfigureAwait(false);
            return item;
        }, CancellationToken.None);

        var results = await task;

        entered.ShouldBe(5);
        results.ShouldBe(items);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ExecuteAsync_RespectsLimit(int maxParallelism)
    {
        var concurrency = 0;
        var maxObserved = 0;
        var items = Enumerable.Range(1, 10).ToList();

        var results = await TargetParallelExecutor.ExecuteAsync(items, maxParallelism, async (item, ct) =>
        {
            var current = Interlocked.Increment(ref concurrency);
            Interlocked.Exchange(ref maxObserved, Math.Max(maxObserved, current));
            await Task.Delay(50, ct).ConfigureAwait(false);
            Interlocked.Decrement(ref concurrency);
            return item;
        }, CancellationToken.None);

        maxObserved.ShouldBeGreaterThan(0);
        maxObserved.ShouldBeLessThanOrEqualTo(maxParallelism);
        results.ShouldBe(items);
    }

    [Fact]
    public async Task ExecuteAsync_PreservesResultOrder()
    {
        var items = Enumerable.Range(1, 10).ToList();
        var random = new Random(42);

        var results = await TargetParallelExecutor.ExecuteAsync(items, 0, async (item, ct) =>
        {
            await Task.Delay(random.Next(10, 50), ct).ConfigureAwait(false);
            return item * 10;
        }, CancellationToken.None);

        results.ShouldBe(items.Select(i => i * 10).ToList());
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_StopsNewItems()
    {
        using var cts = new CancellationTokenSource();
        var started = 0;
        var items = Enumerable.Range(1, 10).ToList();

        var ex = await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await TargetParallelExecutor.ExecuteAsync(items, 1, async (item, ct) =>
            {
                Interlocked.Increment(ref started);

                if (item == 2)
                    cts.Cancel();

                ct.ThrowIfCancellationRequested();
                await Task.Delay(10, ct).ConfigureAwait(false);
                return item;
            }, cts.Token);
        });

        started.ShouldBeLessThan(items.Count);
    }

    [Fact]
    public async Task ExecuteAsync_Exception_Propagates()
    {
        var items = Enumerable.Range(1, 5).ToList();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await TargetParallelExecutor.ExecuteAsync(items, 0, async (item, ct) =>
            {
                if (item == 3)
                    throw new InvalidOperationException("boom");

                await Task.Delay(100, ct).ConfigureAwait(false);
                return item;
            }, CancellationToken.None);
        });

        ex.Message.ShouldBe("boom");
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("5", 5)]
    [InlineData("abc", 0)]
    [InlineData("-1", 0)]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    public void ParseMaxParallelism(string value, int expected)
    {
        var step = new DeploymentStepDto
        {
            Properties = value == null
                ? new List<DeploymentStepPropertyDto>()
                : new List<DeploymentStepPropertyDto>
                {
                    new() { PropertyName = SpecialVariables.Step.MaxParallelism, PropertyValue = value }
                }
        };

        var result = TargetParallelExecutor.ParseMaxParallelism(step);

        result.ShouldBe(expected);
    }

    [Fact]
    public void ParseMaxParallelism_NoProperties_ReturnsZero()
    {
        var step = new DeploymentStepDto { Properties = null };

        var result = TargetParallelExecutor.ParseMaxParallelism(step);

        result.ShouldBe(0);
    }
}

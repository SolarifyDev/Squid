using System;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Moq;
using Shouldly;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Halibut;

/// <summary>Seam test: the Cancel call sites must request the hard bound, otherwise a cancel to an agent that never polls waits out the polling-queue timeout.</summary>
public class RetryingClientCancelLatencyTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private static RpcRetryHandler ShortCancelBudget() => new(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(300));

    [Fact]
    public async Task RetryingScriptClient_CancelToHungAgent_ThrowsWithinBudget()
    {
        var inner = new Mock<IAsyncScriptService>();
        inner.Setup(x => x.CancelScriptAsync(It.IsAny<CancelScriptCommand>())).Returns(new TaskCompletionSource<ScriptStatusResponse>().Task);
        var client = new RetryingScriptClient(inner.Object, ShortCancelBudget(), TimeSpan.FromMinutes(30), CancellationToken.None);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Should.ThrowAsync<HalibutClientException>(() => client.CancelScriptAsync(new CancelScriptCommand(new ScriptTicket("t"), 0)).WaitAsync(Guard));

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task RetryingScriptAgentClient_CancelToHungAgent_ThrowsWithinBudget()
    {
        var inner = new Mock<IScriptAgentClient>();
        inner.Setup(x => x.CancelAsync(It.IsAny<ScriptTicket>(), It.IsAny<long>())).Returns(new TaskCompletionSource<AgentScriptStatus>().Task);
        var client = new RetryingScriptAgentClient(inner.Object, ShortCancelBudget(), TimeSpan.FromMinutes(30), CancellationToken.None);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Should.ThrowAsync<HalibutClientException>(() => client.CancelAsync(new ScriptTicket("t"), 0).WaitAsync(Guard));

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }
}

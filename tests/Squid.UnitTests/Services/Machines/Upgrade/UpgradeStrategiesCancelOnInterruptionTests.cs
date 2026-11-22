using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Core.Settings.Halibut;
using Squid.Message.Contracts.Tentacle;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// The deployment path stopped cancelling the remote script on a PAUSE (see
/// <c>HalibutScriptObserverCancelDecisionTests</c>); the upgrade strategies never pause and supply no
/// decision, so a cancelled upgrade must still send CancelScript exactly as before. Real observer + real
/// strategy; only the agent RPC is faked.
/// </summary>
public sealed class UpgradeStrategiesCancelOnInterruptionTests
{
    public enum Flavor { Linux, Windows }

    [Theory]
    [InlineData(Flavor.Linux)]
    [InlineData(Flavor.Windows)]
    public async Task CancelledUpgrade_StillSendsCancelScriptToAgent(Flavor flavor)
    {
        using var cts = new CancellationTokenSource();
        var agent = new CancelCountingAgent { OnGetStatus = () => cts.Cancel() };
        var factory = new Mock<IHalibutClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>())).Returns(agent);
        var observer = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 20, MaxPollIntervalMs = 50 });
        IMachineUpgradeStrategy strategy = flavor == Flavor.Linux
            ? new LinuxTentacleUpgradeStrategy(factory.Object, observer)
            : new WindowsTentacleUpgradeStrategy(factory.Object, observer);
        var machine = new Machine { Id = 1, Name = "m", SpaceId = 1, Endpoint = """{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"AABB"}""" };

        await Should.ThrowAsync<OperationCanceledException>(() => strategy.UpgradeAsync(machine, "1.4.2", cts.Token));

        agent.CancelCalls.ShouldBe(1, "an interrupted upgrade has no resume path, so the agent script must still be cancelled (behaviour unchanged by the pause fix)");
    }

    private sealed class CancelCountingAgent : IAsyncScriptService
    {
        public int CancelCalls;
        public Action OnGetStatus { get; set; }

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command) => Task.FromResult(Resp(command.ScriptTicket, ProcessState.Running));

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
        {
            OnGetStatus?.Invoke();
            return Task.FromResult(Resp(request.Ticket, ProcessState.Running));
        }

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) => throw new NotSupportedException();

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command)
        {
            Interlocked.Increment(ref CancelCalls);
            return Task.FromResult(Resp(command.Ticket, ProcessState.Complete));
        }

        private static ScriptStatusResponse Resp(ScriptTicket ticket, ProcessState state) => new(ticket, state, 0, new List<ProcessOutput>(), 0);
    }
}

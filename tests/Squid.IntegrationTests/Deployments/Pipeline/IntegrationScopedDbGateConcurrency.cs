using Autofac;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.Certificates;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Interruptions;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Real Postgres, one DI scope, one DbContext. A parallel batch drives every scoped service its steps and targets reach
/// at once; each of them must queue on the scope's gate rather than run two operations on the context concurrently.
/// Fidelity: high (real scope, real DbContext, real services; no mocks).
/// </summary>
public class IntegrationScopedDbGateConcurrency : DeploymentFixtureBase
{
    private const int Rounds = 25;

    [Fact]
    public async Task ParallelCallsOnOneScope_InterruptionsFeedsInFlightLedgerAndCapabilities_NeverShareTheContext()
    {
        // Checkpoint writes and the Executing->Paused transition are gated too; IntegrationParallelGuidedFailureResume drives them through the real pipeline.
        await Run<ILifetimeScope>(async scope =>
        {
            var interruptions = scope.Resolve<IDeploymentInterruptionService>();
            var feeds = scope.Resolve<IExternalFeedDataProvider>();
            var inFlight = scope.Resolve<IInFlightScriptStore>();
            var capabilities = scope.Resolve<IMachineRuntimeCapabilitiesPersistence>();
            var accounts = scope.Resolve<IDeploymentAccountDataProvider>();
            var certificates = scope.Resolve<ICertificateDataProvider>();
            var targets = scope.Resolve<IDeploymentTargetFinder>();
            var traces = scope.Resolve<IUpgradeTracePersistence>();
            var deployment = new Deployment { EnvironmentId = 1, MachineId = 0, Json = string.Empty };
            var slot = new DispatchSlot(MachineId: 1, StepId: 1, ActionId: 1);

            for (var round = 0; round < Rounds; round++)
            {
                var calls = new List<Task>
                {
                    interruptions.FindResolvedInterruptionAsync(1, "s1", "a1", "m1", CancellationToken.None),
                    interruptions.FindResolvedInterruptionAsync(1, "s2", "a2", "m1", CancellationToken.None),
                    feeds.GetFeedByIdAsync(1, CancellationToken.None),
                    feeds.GetExternalFeedsByIdsAsync(new List<int> { 1, 2 }, CancellationToken.None),
                    inFlight.TryGetTicketAsync(1, slot, CancellationToken.None),
                    inFlight.RecordDispatchedAsync(1, slot, "ticket", CancellationToken.None),
                    capabilities.InvalidateAsync(1, CancellationToken.None),
                    accounts.GetAccountByIdAsync(1, CancellationToken.None),
                    certificates.GetCertificateByIdAsync(1, CancellationToken.None),
                    targets.FindTargetsAsync(deployment, CancellationToken.None),
                    traces.SaveAsync(1, new UpgradeTraceSnapshot(), CancellationToken.None)
                };

                await Task.WhenAll(calls).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    // The gate parameter of every gated service is optional, so a missing registration would silently fall back to a private gate per service.
    // This resolves each from the real container and proves it holds the scope's one shared gate.
    [Fact]
    public async Task EveryGatedService_ResolvedFromTheContainer_HoldsTheScopesSharedGate()
    {
        await Run<ILifetimeScope>(async scope =>
        {
            var gate = scope.Resolve<IScopedDbGate>();
            gate.ShouldBeSameAs(scope.Resolve<IScopedDbGate>());

            object[] services =
            [
                scope.Resolve<IDeploymentInterruptionService>(), scope.Resolve<IDeploymentCheckpointService>(), scope.Resolve<IServerTaskDataProvider>(),
                scope.Resolve<IExternalFeedDataProvider>(), scope.Resolve<IInFlightScriptStore>(), scope.Resolve<IMachineRuntimeCapabilitiesPersistence>(),
                scope.Resolve<IDeploymentAccountDataProvider>(), scope.Resolve<ICertificateDataProvider>(), scope.Resolve<IDeploymentTargetFinder>(),
                scope.Resolve<IUpgradeTracePersistence>()
            ];

            foreach (var service in services)
                GateOf(service).ShouldBeSameAs(gate, $"{service.GetType().Name} must hold this scope's gate, not a private fallback");

            await using var other = scope.BeginLifetimeScope();
            other.Resolve<IScopedDbGate>().ShouldNotBeSameAs(gate, "each scope (DbContext) has its own gate");

            await Task.CompletedTask.ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static object GateOf(object service)
        => service.GetType().GetField("_dbGate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(service)
           ?? throw new InvalidOperationException($"{service.GetType().Name} has no _dbGate field or it is null");
}

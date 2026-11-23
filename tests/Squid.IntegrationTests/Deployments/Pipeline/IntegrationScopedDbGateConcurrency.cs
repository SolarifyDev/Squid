using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Deployments.Checkpoints;
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
        await Run<IDeploymentInterruptionService, IExternalFeedDataProvider, IInFlightScriptStore, IMachineRuntimeCapabilitiesPersistence>(async (interruptions, feeds, inFlight, capabilities) =>
        {
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
                    capabilities.InvalidateAsync(1, CancellationToken.None)
                };

                await Task.WhenAll(calls).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }
}

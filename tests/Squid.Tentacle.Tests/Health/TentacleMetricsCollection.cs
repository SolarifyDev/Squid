namespace Squid.Tentacle.Tests.Health;

/// <summary>
/// xUnit collection that serialises every test class which touches the global
/// <see cref="Squid.Tentacle.Health.TentacleMetrics"/> static state — whether
/// directly (asserting counter values) or indirectly (exercising
/// <c>ScriptPodService</c>, <c>NfsWatchdog</c>, <c>KubernetesPodMonitor</c>,
/// etc., all of which increment the same static counters).
/// </summary>
///
/// <remarks>
/// <para>By default xUnit runs each test class in its own collection, which
/// means classes run in parallel — and the counters in <c>TentacleMetrics</c>
/// (process-wide <c>static long</c> fields with <c>Interlocked</c> access) get
/// clobbered when multiple test classes increment them simultaneously.</para>
///
/// <para>Pinning every affected class into this single collection forces them
/// to run serially against each other while still allowing the rest of the
/// suite to parallelise.</para>
///
/// <para>Current members (keep this list in sync — the collection attribute
/// on any class listed here is load-bearing):
/// <list type="bullet">
/// <item><c>TentacleMetricsTests</c> — direct counter assertions</item>
/// <item><c>MetricsPersistenceTests</c> — direct counter assertions via persistence</item>
/// <item><c>ScriptPodServiceTests</c> — exercises <c>ScriptStarted</c>/<c>ScriptCompleted</c></item>
/// <item><c>ScriptRecoveryServiceTests</c> — constructs <c>ScriptPodService</c></item>
/// <item><c>KubernetesPodMonitorTests</c> — exercises <c>SetManagedPods</c>/<c>OrphanedPodCleaned</c></item>
/// <item><c>KubernetesPodMonitorIntegrationTests</c> — exercises <c>ScriptPodService</c></item>
/// <item><c>PendingPodWatchdogTests</c> — exercises <c>ScriptPodService</c></item>
/// <item><c>NfsWatchdogTests</c> — exercises <c>NfsForceKill</c></item>
/// <item><c>NfsWatchdogLifecycleTests</c> — exercises <c>NfsForceKill</c></item>
/// </list>
/// <c>MetricsEndpointIntegrationTests</c> is NOT in this collection because it
/// already belongs to <c>TentacleProcessIntegrationCollection</c> which sets
/// <c>DisableParallelization = true</c>, so it can't run in parallel with
/// anything else anyway.</para>
///
/// <para>When you write a new test class that touches any of the
/// <c>TentacleMetrics</c> callers listed above, add
/// <c>[Collection(TentacleMetricsCollection.Name)]</c> to it and update this
/// comment. Otherwise <c>ExportPrometheus_ContainsAllMetrics</c> and its
/// siblings will flake intermittently.</para>
/// </remarks>
[CollectionDefinition(Name)]
public class TentacleMetricsCollection
{
    public const string Name = "TentacleMetrics";
}

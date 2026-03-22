using Squid.Tentacle.Health;

namespace Squid.Tentacle.Tests.Health;

public class TentacleMetricsTests : IDisposable
{
    public TentacleMetricsTests()
    {
        TentacleMetrics.Reset();
    }

    [Fact]
    public void ScriptStarted_IncrementsActiveAndTotal()
    {
        TentacleMetrics.ScriptStarted();

        TentacleMetrics.ActiveScripts.ShouldBe(1);
        TentacleMetrics.ScriptsStartedTotal.ShouldBe(1);
    }

    [Fact]
    public void ScriptCompleted_DecrementsActiveAndIncrementsCompleted()
    {
        TentacleMetrics.ScriptStarted();
        TentacleMetrics.ScriptCompleted();

        TentacleMetrics.ActiveScripts.ShouldBe(0);
        TentacleMetrics.ScriptsCompletedTotal.ShouldBe(1);
    }

    [Fact]
    public void ScriptFailed_DecrementsActiveAndIncrementsFailed()
    {
        TentacleMetrics.ScriptStarted();
        TentacleMetrics.ScriptFailed();

        TentacleMetrics.ActiveScripts.ShouldBe(0);
        TentacleMetrics.ScriptsFailedTotal.ShouldBe(1);
    }

    [Fact]
    public void ScriptCanceled_DecrementsActiveAndIncrementsCanceled()
    {
        TentacleMetrics.ScriptStarted();
        TentacleMetrics.ScriptCanceled();

        TentacleMetrics.ActiveScripts.ShouldBe(0);
        TentacleMetrics.ScriptsCanceledTotal.ShouldBe(1);
    }

    [Fact]
    public void SetManagedPods_SetsGaugeValue()
    {
        TentacleMetrics.SetManagedPods(7);

        TentacleMetrics.ManagedPods.ShouldBe(7);
    }

    [Fact]
    public void OrphanedPodCleaned_IncrementsCounter()
    {
        TentacleMetrics.OrphanedPodCleaned();
        TentacleMetrics.OrphanedPodCleaned();

        TentacleMetrics.OrphanedPodsCleanedTotal.ShouldBe(2);
    }

    [Fact]
    public void ExportPrometheus_ContainsAllMetrics()
    {
        TentacleMetrics.ScriptStarted();
        TentacleMetrics.ScriptStarted();
        TentacleMetrics.ScriptCompleted();
        TentacleMetrics.SetManagedPods(3);

        var output = MetricsExporter.ExportPrometheus();

        output.ShouldContain("# HELP squid_tentacle_active_scripts");
        output.ShouldContain("# TYPE squid_tentacle_active_scripts gauge");
        output.ShouldContain("squid_tentacle_active_scripts 1");

        output.ShouldContain("# TYPE squid_tentacle_scripts_started_total counter");
        output.ShouldContain("squid_tentacle_scripts_started_total 2");

        output.ShouldContain("squid_tentacle_scripts_completed_total 1");
        output.ShouldContain("squid_tentacle_managed_pods 3");
    }

    [Fact]
    public void Reset_ClearsAllCounters()
    {
        TentacleMetrics.ScriptStarted();
        TentacleMetrics.SetManagedPods(5);
        TentacleMetrics.OrphanedPodCleaned();

        TentacleMetrics.Reset();

        TentacleMetrics.ActiveScripts.ShouldBe(0);
        TentacleMetrics.ScriptsStartedTotal.ShouldBe(0);
        TentacleMetrics.ManagedPods.ShouldBe(0);
        TentacleMetrics.OrphanedPodsCleanedTotal.ShouldBe(0);
    }

    public void Dispose()
    {
        TentacleMetrics.Reset();
    }
}

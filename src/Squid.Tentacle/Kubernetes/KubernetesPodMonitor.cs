using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Health;
using Squid.Tentacle.ScriptExecution;
using Serilog;
using RFS = Squid.Tentacle.ScriptExecution.ResilientFileSystem;

namespace Squid.Tentacle.Kubernetes;

public class KubernetesPodMonitor
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(300);

    private readonly KubernetesPodManager _podManager;
    private readonly ScriptPodService _scriptPodService;
    private readonly TentacleSettings _tentacleSettings;
    private readonly PodDisruptionBudgetManager _pdbManager;
    private readonly KubernetesApiHealthProbe _apiHealthProbe;
    private readonly KubernetesLeaderElection _leaderElection;
    private readonly TimeSpan _pendingPodTimeout;
    private readonly TimeSpan _orphanAge;
    private readonly int _orphanGracePeriod;

    public KubernetesPodMonitor(
        KubernetesPodManager podManager,
        ScriptPodService scriptPodService,
        TentacleSettings tentacleSettings,
        KubernetesSettings kubernetesSettings,
        PodDisruptionBudgetManager pdbManager = null,
        KubernetesApiHealthProbe apiHealthProbe = null,
        KubernetesLeaderElection leaderElection = null)
    {
        _podManager = podManager;
        _scriptPodService = scriptPodService;
        _tentacleSettings = tentacleSettings;
        _pdbManager = pdbManager;
        _apiHealthProbe = apiHealthProbe;
        _leaderElection = leaderElection;
        _pendingPodTimeout = TimeSpan.FromMinutes(kubernetesSettings.PendingPodTimeoutMinutes);
        _orphanAge = TimeSpan.FromMinutes(kubernetesSettings.OrphanCleanupMinutes);
        _orphanGracePeriod = kubernetesSettings.OrphanPodGracePeriodSeconds;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Information("Pod monitor started. Cleanup interval={IntervalSeconds}s", CleanupInterval.TotalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CleanupInterval, ct).ConfigureAwait(false);
                RunCleanupCycle();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Pod monitor cleanup cycle failed");
            }
        }
    }

    internal void RunCleanupCycle()
    {
        if (_leaderElection != null && !_leaderElection.IsLeader)
        {
            Log.Debug("Skipping cleanup cycle — not leader");
            return;
        }

        FailPendingPods();
        CleanupOrphanedPods();
        CleanupOrphanedWorkspaces();
        CheckWorkspaceCapacity();
        _pdbManager?.ReconcilePdb();
        _scriptPodService.EvictStaleTerminalResults(TimeSpan.FromHours(1));
        _apiHealthProbe?.Check();
    }

    internal void FailPendingPods()
    {
        var pods = _podManager.ListManagedPods();
        var activeTickets = _scriptPodService.ActiveScripts;

        foreach (var pod in pods)
        {
            if (pod.Status?.Phase != "Pending") continue;

            var createdAt = pod.Metadata?.CreationTimestamp;
            if (createdAt == null) continue;

            var age = DateTime.UtcNow - createdAt.Value;
            if (age < _pendingPodTimeout) continue;

            string ticketId = null;
            pod.Metadata.Labels?.TryGetValue("squid.io/ticket-id", out ticketId);

            if (ticketId == null) continue;

            var podName = pod.Metadata.Name;

            // Re-check phase — pod may have transitioned since list snapshot
            var currentPhase = _podManager.GetPodPhase(podName);
            if (currentPhase != "Pending")
            {
                Log.Debug("Pod {PodName} transitioned to {Phase} since list, skipping", podName, currentPhase);
                continue;
            }

            // Atomic remove — if another thread already completed this ticket, TryRemove returns false and we skip
            if (!activeTickets.TryRemove(ticketId, out var ctx)) continue;

            Log.Warning("Pod {PodName} stuck in Pending for {AgeMinutes:F0}m, marking as failed (ticket {TicketId})",
                podName, age.TotalMinutes, ticketId);

            _podManager.DeletePod(podName, 0);

            var errorLog = new ProcessOutput(ProcessOutputSource.StdErr,
                $"Script pod {podName} stuck in Pending state for {age.TotalMinutes:F0} minutes. Likely cause: insufficient cluster resources, image pull failure, or unschedulable node.");

            _scriptPodService.InjectTerminalResult(ticketId, ScriptExitCodes.Timeout, new List<ProcessOutput> { errorLog });

            _scriptPodService.ReleaseMutexForTicket(ticketId);
        }
    }

    private void CleanupOrphanedPods()
    {
        var pods = _podManager.ListManagedPods();
        TentacleMetrics.SetManagedPods(pods.Count);

        var activeTickets = _scriptPodService.ActiveScripts;

        foreach (var pod in pods)
        {
            var podName = pod.Metadata.Name;
            string ticketId = null;
            pod.Metadata.Labels?.TryGetValue("squid.io/ticket-id", out ticketId);

            var phase = pod.Status?.Phase;

            if (phase is "Succeeded" or "Failed")
            {
                CleanupTerminatedPod(pod, podName, ticketId, activeTickets);
                continue;
            }

            CleanupStaleRunningPod(pod, podName, ticketId, activeTickets);
        }
    }

    private void CleanupTerminatedPod(k8s.Models.V1Pod pod, string podName, string ticketId,
        System.Collections.Concurrent.ConcurrentDictionary<string, ScriptPodContext> activeTickets)
    {
        var finishedAt = pod.Status?.ContainerStatuses?
            .FirstOrDefault()?.State?.Terminated?.FinishedAt;

        if (finishedAt == null)
            return;

        var age = DateTime.UtcNow - finishedAt.Value;

        if (age < _orphanAge)
            return;

        if (ticketId != null && activeTickets.ContainsKey(ticketId))
            return;

        Log.Information("Cleaning up orphaned pod {PodName} (phase={Phase}, age={AgeMinutes:F0}m)", podName, pod.Status?.Phase, age.TotalMinutes);
        _podManager.DeletePod(podName, 0);
        TentacleMetrics.OrphanedPodCleaned();
    }

    private void CleanupStaleRunningPod(k8s.Models.V1Pod pod, string podName, string ticketId,
        System.Collections.Concurrent.ConcurrentDictionary<string, ScriptPodContext> activeTickets)
    {
        if (ticketId != null && activeTickets.ContainsKey(ticketId))
            return;

        var startedAt = pod.Status?.StartTime;

        if (startedAt == null)
            return;

        var age = DateTime.UtcNow - startedAt.Value;

        if (age < _orphanAge)
            return;

        Log.Information("Cleaning up stale running pod {PodName} (phase={Phase}, age={AgeMinutes:F0}m, no active ticket)",
            podName, pod.Status?.Phase, age.TotalMinutes);
        _podManager.DeletePod(podName, _orphanGracePeriod);
        TentacleMetrics.OrphanedPodCleaned();
    }

    private void CheckWorkspaceCapacity()
    {
        var usage = DiskSpaceChecker.GetWorkspaceUsage(_tentacleSettings.WorkspacePath);

        if (usage.TotalBytes <= 0) return;

        if (usage.IsLowSpace)
        {
            Log.Warning("Workspace disk space is low: {Free} free of {Total} ({Percentage:P1})",
                DiskSpaceChecker.FormatBytes(usage.FreeBytes), DiskSpaceChecker.FormatBytes(usage.TotalBytes), usage.FreePercentage);
        }
    }

    private void CleanupOrphanedWorkspaces()
    {
        if (!RFS.DirectoryExists(_tentacleSettings.WorkspacePath))
            return;

        var activeTickets = _scriptPodService.ActiveScripts;

        foreach (var dir in RFS.GetDirectories(_tentacleSettings.WorkspacePath))
        {
            var ticketId = Path.GetFileName(dir);

            if (activeTickets.ContainsKey(ticketId))
                continue;

            var dirInfo = new DirectoryInfo(dir);
            var age = DateTime.UtcNow - dirInfo.LastWriteTimeUtc;

            if (age < _orphanAge)
                continue;

            try
            {
                RFS.DeleteDirectory(dir, recursive: true);

                Log.Information("Cleaned up orphaned workspace {Dir}", dir);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to cleanup workspace {Dir}", dir);
            }
        }
    }
}

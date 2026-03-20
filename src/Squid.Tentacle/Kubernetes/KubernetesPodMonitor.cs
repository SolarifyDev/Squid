using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.ScriptExecution;
using Serilog;
using RFS = Squid.Tentacle.ScriptExecution.ResilientFileSystem;

namespace Squid.Tentacle.Kubernetes;

public class KubernetesPodMonitor
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OrphanAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PendingPodTimeout = TimeSpan.FromMinutes(5);

    private readonly KubernetesPodManager _podManager;
    private readonly ScriptPodService _scriptPodService;
    private readonly TentacleSettings _tentacleSettings;

    public KubernetesPodMonitor(
        KubernetesPodManager podManager,
        ScriptPodService scriptPodService,
        TentacleSettings tentacleSettings)
    {
        _podManager = podManager;
        _scriptPodService = scriptPodService;
        _tentacleSettings = tentacleSettings;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Information("Pod monitor started. Cleanup interval={IntervalSeconds}s", CleanupInterval.TotalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CleanupInterval, ct).ConfigureAwait(false);
                FailPendingPods();
                CleanupOrphanedPods();
                CleanupOrphanedWorkspaces();
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
            if (age < PendingPodTimeout) continue;

            string ticketId = null;
            pod.Metadata.Labels?.TryGetValue("squid.io/ticket-id", out ticketId);

            if (ticketId == null || !activeTickets.ContainsKey(ticketId)) continue;

            var podName = pod.Metadata.Name;

            Log.Warning("Pod {PodName} stuck in Pending for {AgeMinutes:F0}m, marking as failed (ticket {TicketId})",
                podName, age.TotalMinutes, ticketId);

            if (activeTickets.TryRemove(ticketId, out var ctx))
            {
                _podManager.DeletePod(podName);

                var errorLog = new ProcessOutput(ProcessOutputSource.StdErr,
                    $"Script pod {podName} stuck in Pending state for {age.TotalMinutes:F0} minutes. Likely cause: insufficient cluster resources, image pull failure, or unschedulable node.");

                _scriptPodService.InjectTerminalResult(ticketId, ScriptExitCodes.Timeout, new List<ProcessOutput> { errorLog });
            }
        }
    }

    private void CleanupOrphanedPods()
    {
        var pods = _podManager.ListManagedPods();
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

        if (age < OrphanAge)
            return;

        if (ticketId != null && activeTickets.ContainsKey(ticketId))
            return;

        Log.Information("Cleaning up orphaned pod {PodName} (phase={Phase}, age={AgeMinutes:F0}m)", podName, pod.Status?.Phase, age.TotalMinutes);
        _podManager.DeletePod(podName);
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

        if (age < OrphanAge)
            return;

        Log.Information("Cleaning up stale running pod {PodName} (phase={Phase}, age={AgeMinutes:F0}m, no active ticket)",
            podName, pod.Status?.Phase, age.TotalMinutes);
        _podManager.DeletePod(podName);
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

            if (age < OrphanAge)
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

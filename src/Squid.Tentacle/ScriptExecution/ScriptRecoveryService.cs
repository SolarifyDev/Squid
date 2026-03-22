using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Serilog;
using RFS = Squid.Tentacle.ScriptExecution.ResilientFileSystem;

namespace Squid.Tentacle.ScriptExecution;

public class ScriptRecoveryService
{
    public int RecoverScripts(string workspacePath, ScriptPodService service, KubernetesPodManager podManager, ScriptIsolationMutex mutex)
    {
        if (!RFS.DirectoryExists(workspacePath))
        {
            Log.Debug("Workspace path {Path} does not exist, skipping recovery", workspacePath);
            return 0;
        }

        var directories = RFS.GetDirectories(workspacePath);
        var recoveredCount = 0;

        foreach (var dir in directories)
        {
            var state = ScriptStateFile.TryRead(dir);
            if (state == null) continue;

            try
            {
                RecoverScript(state, dir, service, podManager, mutex);
                recoveredCount++;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to recover script {TicketId}", state.TicketId);
                InjectFailure(state.TicketId, service, "Recovery failed: " + ex.Message);
            }
        }

        if (recoveredCount > 0)
            Log.Information("Recovered {Count} scripts from previous session", recoveredCount);

        return recoveredCount;
    }

    public void RecoverPendingScripts(IKubernetesPodOperations podOps, KubernetesSettings settings, ScriptPodService service)
    {
        try
        {
            var configMaps = podOps.ListConfigMaps(settings.TentacleNamespace, "squid.io/context-type=pending-script");

            if (configMaps?.Items == null || configMaps.Items.Count == 0)
                return;

            Log.Information("Found {Count} pending script ConfigMaps to recover", configMaps.Items.Count);

            foreach (var cm in configMaps.Items)
            {
                try
                {
                    var data = cm.Data;
                    if (data == null) continue;

                    data.TryGetValue("ticketId", out var ticketId);
                    data.TryGetValue("scriptBody", out var scriptBody);

                    if (string.IsNullOrEmpty(ticketId) || string.IsNullOrEmpty(scriptBody))
                        continue;

                    data.TryGetValue("isolation", out var isolationStr);
                    Enum.TryParse<ScriptIsolationLevel>(isolationStr, out var isolation);
                    data.TryGetValue("isolationMutexName", out var mutexName);
                    data.TryGetValue("targetNamespace", out var targetNamespace);

                    var command = new StartScriptCommand(
                        scriptBody, isolation, TimeSpan.FromMinutes(30),
                        string.IsNullOrEmpty(mutexName) ? null : mutexName,
                        Array.Empty<string>(), null)
                    {
                        TargetNamespace = string.IsNullOrEmpty(targetNamespace) ? null : targetNamespace
                    };

                    service.StartScript(command);

                    podOps.DeleteConfigMap(cm.Metadata.Name, settings.TentacleNamespace);

                    Log.Information("Recovered pending script {TicketId} from ConfigMap", ticketId);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to recover pending script from ConfigMap {Name}", cm.Metadata?.Name);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to list pending script ConfigMaps for recovery");
        }
    }

    private static void RecoverScript(ScriptStateFile state, string workDir, ScriptPodService service, KubernetesPodManager podManager, ScriptIsolationMutex mutex)
    {
        var phase = podManager.GetPodPhase(state.PodName);

        if (phase is KubernetesPodManager.PhaseNotFound)
        {
            Log.Information("Recovered script {TicketId}: pod {PodName} not found, injecting terminal result", state.TicketId, state.PodName);
            InjectFailure(state.TicketId, service, "Pod not found after agent restart");
            return;
        }

        if (phase is "Succeeded" or "Failed")
        {
            Log.Information("Recovered script {TicketId}: pod {PodName} already completed ({Phase})", state.TicketId, state.PodName, phase);
            InjectFailure(state.TicketId, service, $"Pod completed during agent restart (phase: {phase})");
            return;
        }

        Log.Information("Recovered script {TicketId}: pod {PodName} still running ({Phase}), rebuilding context", state.TicketId, state.PodName, phase);

        var ctx = new ScriptPodContext(state.TicketId, state.PodName, workDir, state.EosMarkerToken, state.Namespace);
        var mutexHandle = TryReacquireMutex(state, mutex);

        service.RestoreActiveScript(ctx, mutexHandle);
    }

    private static IDisposable? TryReacquireMutex(ScriptStateFile state, ScriptIsolationMutex mutex)
    {
        var isolation = Enum.TryParse<ScriptIsolationLevel>(state.Isolation, out var level)
            ? level
            : ScriptIsolationLevel.NoIsolation;

        if (mutex.TryAcquire(isolation, state.IsolationMutexName, out var handle) && handle != null)
        {
            Log.Debug("Reacquired {Isolation} mutex for recovered script {TicketId}", state.Isolation, state.TicketId);
            return handle;
        }

        Log.Warning("Could not reacquire mutex for recovered script {TicketId}, proceeding without lock", state.TicketId);
        return null;
    }

    private static void InjectFailure(string ticketId, ScriptPodService service, string message)
    {
        service.InjectTerminalResult(ticketId, ScriptExitCodes.UnknownResult, new List<ProcessOutput> { new(ProcessOutputSource.StdErr, message) });
    }
}

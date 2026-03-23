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
            var secrets = podOps.ListSecrets(settings.TentacleNamespace, "squid.io/context-type=pending-script");

            if (secrets?.Items == null || secrets.Items.Count == 0)
                return;

            Log.Information("Found {Count} pending script Secrets to recover", secrets.Items.Count);

            foreach (var secret in secrets.Items)
            {
                try
                {
                    var data = secret.StringData ?? new Dictionary<string, string>();

                    if (data.Count == 0 && secret.Data is { Count: > 0 })
                    {
                        data = secret.Data.ToDictionary(
                            kvp => kvp.Key,
                            kvp => System.Text.Encoding.UTF8.GetString(kvp.Value));
                    }

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

                    InjectFailure(ticketId, service,
                        "Pending script could not be recovered: agent restarted while script was queued. Variables and files are no longer available.");

                    podOps.DeleteSecret(secret.Metadata.Name, settings.TentacleNamespace);

                    Log.Information("Injected terminal failure for pending script {TicketId} from Secret", ticketId);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to recover pending script from Secret {Name}", secret.Metadata?.Name);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to list pending script Secrets for recovery");
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
        ctx.LastLogTimestamp = state.LastLogTimestamp;

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

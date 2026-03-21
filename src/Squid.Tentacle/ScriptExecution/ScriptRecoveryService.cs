using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Kubernetes;
using Serilog;

namespace Squid.Tentacle.ScriptExecution;

public class ScriptRecoveryService
{
    public int RecoverScripts(string workspacePath, ScriptPodService service, KubernetesPodManager podManager, ScriptIsolationMutex mutex)
    {
        if (!Directory.Exists(workspacePath))
        {
            Log.Debug("Workspace path {Path} does not exist, skipping recovery", workspacePath);
            return 0;
        }

        var directories = Directory.GetDirectories(workspacePath);
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

        var ctx = new ScriptPodContext(state.TicketId, state.PodName, workDir, state.EosMarkerToken);
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

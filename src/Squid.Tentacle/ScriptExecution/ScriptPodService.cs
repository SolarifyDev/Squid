using System.Collections.Concurrent;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Message.Contracts.Tentacle;
using Serilog;
using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.ScriptExecution;

public partial class ScriptPodService : IScriptService, ITentacleScriptBackend
{
    private readonly TentacleSettings _tentacleSettings;
    private readonly KubernetesSettings _kubernetesSettings;
    private readonly KubernetesPodManager _podManager;
    private readonly ConcurrentDictionary<string, ScriptPodContext> _scripts = new();

    public ScriptPodService(
        TentacleSettings tentacleSettings,
        KubernetesSettings kubernetesSettings,
        KubernetesPodManager podManager)
    {
        _tentacleSettings = tentacleSettings;
        _kubernetesSettings = kubernetesSettings;
        _podManager = podManager;
    }

    public ScriptTicket StartScript(StartScriptCommand command)
    {
        var ticketId = Guid.NewGuid().ToString("N");
        var workDir = PrepareWorkspace(ticketId, command);
        var podName = _podManager.CreatePod(ticketId);

        _scripts[ticketId] = new ScriptPodContext(ticketId, podName, workDir);

        Log.Information("Started script pod {PodName} for ticket {TicketId}", podName, ticketId);

        return new ScriptTicket(ticketId);
    }

    public ScriptStatusResponse GetStatus(ScriptStatusRequest request)
    {
        if (!_scripts.TryGetValue(request.Ticket.TaskId, out var ctx))
            return CompletedResponse(request.Ticket, -1);

        var phase = _podManager.GetPodPhase(ctx.PodName);
        var logs = DrainLogs(ctx);
        var state = MapPhaseToState(phase);
        var exitCode = state == ProcessState.Complete
            ? _podManager.GetPodExitCode(ctx.PodName) : 0;

        return new ScriptStatusResponse(request.Ticket, state, exitCode, logs, ctx.LogSequence);
    }

    public ScriptStatusResponse CompleteScript(CompleteScriptCommand command)
    {
        if (!_scripts.TryRemove(command.Ticket.TaskId, out var ctx))
            return CompletedResponse(command.Ticket, -1);

        _podManager.WaitForPodTermination(ctx.PodName, TimeSpan.FromSeconds(30));

        var logs = DrainFinalLogs(ctx);
        var exitCode = _podManager.GetPodExitCode(ctx.PodName);

        _podManager.DeletePod(ctx.PodName);
        CleanupWorkspace(ctx.WorkDir);

        return new ScriptStatusResponse(command.Ticket, ProcessState.Complete, exitCode, logs, ctx.LogSequence);
    }

    public ScriptStatusResponse CancelScript(CancelScriptCommand command)
    {
        if (!_scripts.TryRemove(command.Ticket.TaskId, out var ctx))
            return CompletedResponse(command.Ticket, -1);

        _podManager.DeletePod(ctx.PodName);
        CleanupWorkspace(ctx.WorkDir);

        return new ScriptStatusResponse(command.Ticket, ProcessState.Complete, -1, new List<ProcessOutput>(), ctx.LogSequence);
    }

    public ConcurrentDictionary<string, ScriptPodContext> ActiveScripts => _scripts;

    private static ScriptStatusResponse CompletedResponse(ScriptTicket ticket, int exitCode)
        => new(ticket, ProcessState.Complete, exitCode, new List<ProcessOutput>(), 0);

    private static ProcessState MapPhaseToState(string? phase)
    {
        return phase switch
        {
            "Succeeded" => ProcessState.Complete,
            "Failed" => ProcessState.Complete,
            null => ProcessState.Running,
            _ => ProcessState.Running
        };
    }
}

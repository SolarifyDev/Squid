using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public sealed class HalibutScriptObserver : IHalibutScriptObserver
{
    // NOTE: Halibut RPC proxy calls (GetStatusAsync, CompleteScriptAsync, CancelScriptAsync)
    // do not accept CancellationToken — cancellation is only checked between polling intervals.
    // This is a known Halibut 8.x limitation.
    public async Task<ScriptExecutionResult> ObserveAndCompleteAsync(
        Machine machine,
        IAsyncScriptService scriptClient,
        ScriptTicket ticket,
        TimeSpan scriptTimeout,
        CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        var pollInterval = TimeSpan.FromSeconds(1);
        var maxPollInterval = TimeSpan.FromSeconds(10);
        var statusResponse = new ScriptStatusResponse(ticket, ProcessState.Pending, 0, new List<ProcessOutput>(), 0);
        var allLogs = new List<ProcessOutput>();

        while (statusResponse.State != ProcessState.Complete)
        {
            if (DateTime.UtcNow - startTime > scriptTimeout)
            {
                Log.Warning("Script execution timeout ({TimeoutMinutes}m) on agent {MachineName}, cancelling",
                    scriptTimeout.TotalMinutes, machine.Name);
                await TryCancelScriptAsync(scriptClient, ticket, statusResponse.NextLogSequence).ConfigureAwait(false);

                return new ScriptExecutionResult
                {
                    Success = false,
                    ExitCode = -1,
                    LogLines = new List<string> { $"Script execution exceeded {scriptTimeout.TotalMinutes}-minute timeout" }
                };
            }

            ct.ThrowIfCancellationRequested();

            statusResponse = await scriptClient.GetStatusAsync(
                new ScriptStatusRequest(ticket, statusResponse.NextLogSequence)).ConfigureAwait(false);

            allLogs.AddRange(statusResponse.Logs);
            LogOutput(statusResponse.Logs, machine.Name);

            if (statusResponse.State != ProcessState.Complete)
            {
                await Task.Delay(pollInterval, ct).ConfigureAwait(false);
                pollInterval = TimeSpan.FromSeconds(Math.Min(pollInterval.TotalSeconds * 1.5, maxPollInterval.TotalSeconds));
            }
        }

        var completeResponse = await scriptClient.CompleteScriptAsync(
            new CompleteScriptCommand(ticket, statusResponse.NextLogSequence)).ConfigureAwait(false);

        allLogs.AddRange(completeResponse.Logs);

        var logLines = allLogs
            .OrderBy(l => l.Occurred)
            .Where(l => !string.IsNullOrEmpty(l.Text))
            .Select(l => l.Text)
            .ToList();

        var success = completeResponse.ExitCode == 0;

        if (!success)
            Log.Error("Script failed on agent {MachineName} with exit code {ExitCode}",
                machine.Name, completeResponse.ExitCode);
        else
            Log.Information("Script completed successfully on agent {MachineName}", machine.Name);

        return new ScriptExecutionResult
        {
            Success = success,
            LogLines = logLines,
            ExitCode = completeResponse.ExitCode
        };
    }

    private static void LogOutput(List<ProcessOutput> logs, string machineName)
    {
        foreach (var log in logs)
            Log.Information("[Agent Script] Machine={MachineName}, Source={Source}, Message={Message}",
                machineName, log.Source, log.Text);
    }

    private static async Task TryCancelScriptAsync(
        IAsyncScriptService scriptClient, ScriptTicket ticket, long lastLogSequence)
    {
        try
        {
            await scriptClient.CancelScriptAsync(
                new CancelScriptCommand(ticket, lastLogSequence)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to cancel script with ticket {Ticket}", ticket.TaskId);
        }
    }
}

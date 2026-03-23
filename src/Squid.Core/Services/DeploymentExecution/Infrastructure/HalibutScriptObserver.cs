using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Constants;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public sealed class HalibutScriptObserver : IHalibutScriptObserver
{
    internal const int MaxLogEntries = 100_000;

    // NOTE: Halibut RPC proxy calls (GetStatusAsync, CompleteScriptAsync, CancelScriptAsync)
    // do not accept CancellationToken — cancellation is only checked between polling intervals.
    // This is a known Halibut 8.x limitation.
    public async Task<ScriptExecutionResult> ObserveAndCompleteAsync(
        Machine machine,
        IAsyncScriptService scriptClient,
        ScriptTicket ticket,
        TimeSpan scriptTimeout,
        CancellationToken ct,
        SensitiveValueMasker masker = null)
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

                var collectedLogLines = allLogs
                    .OrderBy(l => l.Occurred)
                    .Where(l => !string.IsNullOrEmpty(l.Text))
                    .Select(l => masker?.Mask(l.Text) ?? l.Text)
                    .ToList();

                collectedLogLines.Add($"Script execution exceeded {scriptTimeout.TotalMinutes}-minute timeout");

                return new ScriptExecutionResult
                {
                    Success = false,
                    ExitCode = ScriptExitCodes.Timeout,
                    LogLines = collectedLogLines
                };
            }

            try
            {
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                await TryCancelScriptAsync(scriptClient, ticket, statusResponse.NextLogSequence).ConfigureAwait(false);
                throw;
            }

            statusResponse = await scriptClient.GetStatusAsync(
                new ScriptStatusRequest(ticket, statusResponse.NextLogSequence)).ConfigureAwait(false);

            allLogs.AddRange(statusResponse.Logs);
            TruncateIfExceeded(allLogs);
            LogOutput(statusResponse.Logs, machine.Name, masker);

            if (statusResponse.State != ProcessState.Complete)
            {
                try
                {
                    await Task.Delay(pollInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await TryCancelScriptAsync(scriptClient, ticket, statusResponse.NextLogSequence).ConfigureAwait(false);
                    throw;
                }

                pollInterval = TimeSpan.FromSeconds(Math.Min(pollInterval.TotalSeconds * 1.5, maxPollInterval.TotalSeconds));
            }
        }

        var completeResponse = await scriptClient.CompleteScriptAsync(
            new CompleteScriptCommand(ticket, statusResponse.NextLogSequence)).ConfigureAwait(false);

        allLogs.AddRange(completeResponse.Logs);
        TruncateIfExceeded(allLogs);
        LogOutput(completeResponse.Logs, machine.Name, masker);

        var orderedLogs = allLogs
            .OrderBy(l => l.Occurred)
            .Where(l => !string.IsNullOrEmpty(l.Text))
            .ToList();

        var logLines = orderedLogs.Select(l => masker?.Mask(l.Text) ?? l.Text).ToList();

        var stderrLines = orderedLogs
            .Where(l => l.Source == ProcessOutputSource.StdErr)
            .Select(l => masker?.Mask(l.Text) ?? l.Text)
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
            StderrLines = stderrLines,
            ExitCode = completeResponse.ExitCode
        };
    }

    private static void LogOutput(List<ProcessOutput> logs, string machineName, SensitiveValueMasker masker)
    {
        foreach (var log in logs)
            Log.Information("[Agent Script] Machine={MachineName}, Source={Source}, Message={Message}",
                machineName, log.Source, masker?.Mask(log.Text) ?? log.Text);
    }

    private static void TruncateIfExceeded(List<ProcessOutput> logs)
    {
        if (logs.Count <= MaxLogEntries) return;

        var overflow = logs.Count - MaxLogEntries;
        logs.RemoveRange(0, overflow);
        Log.Warning("Log buffer exceeded {Max} entries, truncated {Overflow} oldest entries", MaxLogEntries, overflow);
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

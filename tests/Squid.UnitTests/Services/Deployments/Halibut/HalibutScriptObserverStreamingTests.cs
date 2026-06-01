using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Settings.Halibut;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;

namespace Squid.UnitTests.Services.Deployments.Halibut;

/// <summary>
/// Live log tail: when an output sink is wired, <see cref="HalibutScriptObserver"/> streams each
/// incremental batch of agent output to the sink AS IT ARRIVES (before completion), tags the source,
/// and marks the result OutputStreamed so the bulk post-completion persist is skipped (no duplication).
/// When no sink is wired, behaviour is unchanged. Streaming failures fall back to bulk (no data loss).
/// </summary>
public class HalibutScriptObserverStreamingTests
{
    private readonly HalibutScriptObserver _observer = new();
    private readonly Mock<IAsyncScriptService> _scriptClient = new();
    private readonly Machine _machine = new() { Name = "test-agent" };
    private readonly ScriptTicket _ticket = new("test-ticket");
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(30);

    private (List<ScriptOutputLine> lines, List<int> batchSizes) RecordingSink(out ScriptOutputSink sink)
    {
        var lines = new List<ScriptOutputLine>();
        var batchSizes = new List<int>();
        sink = (batch, ct) =>
        {
            batchSizes.Add(batch.Count);
            lines.AddRange(batch);
            return Task.CompletedTask;
        };
        return (lines, batchSizes);
    }

    [Fact]
    public async Task Streaming_InvokesSinkPerBatch_InOrder_AndMarksOutputStreamed()
    {
        var callCount = 0;

        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount < 3)
                    return new ScriptStatusResponse(_ticket, ProcessState.Running, 0,
                        new List<ProcessOutput> { new(ProcessOutputSource.StdOut, $"log-{callCount}") }, callCount);

                return new ScriptStatusResponse(_ticket, ProcessState.Complete, 0,
                    new List<ProcessOutput> { new(ProcessOutputSource.StdOut, "log-final") }, callCount);
            });

        _scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        var (streamed, batchSizes) = RecordingSink(out var sink);

        var result = await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None, outputSink: sink);

        result.OutputStreamed.ShouldBeTrue();
        // Delivered per poll batch (not one bulk dump at the end).
        batchSizes.Count.ShouldBeGreaterThanOrEqualTo(3);
        var texts = streamed.Select(l => l.Text).ToList();
        texts.ShouldBe(new[] { "log-1", "log-2", "log-final" });
    }

    [Fact]
    public async Task NoSink_DoesNotStream_AndOutputStreamedIsFalse()
    {
        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0,
                new List<ProcessOutput> { new(ProcessOutputSource.StdOut, "out") }, 1));
        _scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 1));

        var result = await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None);

        result.OutputStreamed.ShouldBeFalse();
        result.LogLines.ShouldContain("out");
    }

    [Fact]
    public async Task Streaming_TagsStdErrLines()
    {
        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 1,
                new List<ProcessOutput>
                {
                    new(ProcessOutputSource.StdOut, "normal"),
                    new(ProcessOutputSource.StdErr, "boom")
                }, 1));
        _scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 1, new List<ProcessOutput>(), 1));

        var (streamed, _) = RecordingSink(out var sink);

        await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None, outputSink: sink);

        streamed.Single(l => l.Text == "normal").IsStdErr.ShouldBeFalse();
        streamed.Single(l => l.Text == "boom").IsStdErr.ShouldBeTrue();
    }

    [Fact]
    public async Task Streaming_SkipsEmptyLines()
    {
        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0,
                new List<ProcessOutput>
                {
                    new(ProcessOutputSource.StdOut, ""),
                    new(ProcessOutputSource.StdOut, "real")
                }, 1));
        _scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 1));

        var (streamed, _) = RecordingSink(out var sink);

        await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None, outputSink: sink);

        streamed.Select(l => l.Text).ShouldBe(new[] { "real" });
    }

    [Fact]
    public async Task Streaming_SinkThrows_FallsBackToBulk_OutputStreamedFalse_NoDataLoss()
    {
        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0,
                new List<ProcessOutput> { new(ProcessOutputSource.StdOut, "line-a") }, 1));
        _scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 1));

        ScriptOutputSink throwingSink = (batch, ct) => throw new InvalidOperationException("sink down");

        var result = await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None, outputSink: throwingSink);

        // Sink failure must NOT fail the script, and must fall back to the bulk persist (flag false)
        // so the lines are still surfaced from the returned result — never silently lost.
        result.Success.ShouldBeTrue();
        result.OutputStreamed.ShouldBeFalse();
        result.LogLines.ShouldContain("line-a");
    }

    [Fact]
    public async Task Streaming_Timeout_StreamsSyntheticLine_AndMarksOutputStreamed()
    {
        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Running, 0, new List<ProcessOutput>(), 0));
        _scriptClient.Setup(s => s.CancelScriptAsync(It.IsAny<CancelScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, ScriptExitCodes.Canceled, new List<ProcessOutput>(), 0));

        var (streamed, _) = RecordingSink(out var sink);

        var result = await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, TimeSpan.FromMilliseconds(100), CancellationToken.None, outputSink: sink);

        result.ExitCode.ShouldBe(ScriptExitCodes.Timeout);
        result.OutputStreamed.ShouldBeTrue();
        streamed.ShouldContain(l => l.Text.Contains("timeout") && l.IsStdErr);
    }

    // ── Gap marker on log-buffer truncation (#5a) ────────────────────────────
    // When a single script's output exceeds the retention buffer, the observer
    // drops the oldest entries. Without a marker the operator sees a silent jump;
    // these pin that an operator-visible marker is inserted (bulk path) AND
    // streamed (so it persists when the streaming path skips the bulk persist).

    private static ScriptStatusResponse CompleteWith(ScriptTicket ticket, List<ProcessOutput> logs)
        => new(ticket, ProcessState.Complete, 0, logs, logs.Count);

    private static List<ProcessOutput> Lines(int count)
    {
        var logs = new List<ProcessOutput>();
        for (var i = 1; i <= count; i++)
            logs.Add(new ProcessOutput(ProcessOutputSource.StdOut, $"line-{i}", DateTimeOffset.UtcNow.AddSeconds(i)));
        return logs;
    }

    [Fact]
    public async Task Truncation_InsertsGapMarkerAtHead_AndDropsOldest()
    {
        var observer = new HalibutScriptObserver(new ObserverSettings { MaxLogEntries = 3 });
        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>())).ReturnsAsync(CompleteWith(_ticket, Lines(5)));
        _scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        var result = await observer.ObserveAndCompleteAsync(_machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None);

        result.LogLines[0].ShouldContain("truncated", customMessage: "the gap marker must sort to the head of the log so the operator sees it where the older lines were.");
        result.LogLines.ShouldContain("line-5", customMessage: "newest lines are retained.");
        result.LogLines.ShouldNotContain("line-1", customMessage: "oldest lines are dropped past the buffer cap.");
    }

    [Fact]
    public async Task Truncation_StreamsGapMarker_AsInformationalNotErrorLine()
    {
        var observer = new HalibutScriptObserver(new ObserverSettings { MaxLogEntries = 3 });
        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>())).ReturnsAsync(CompleteWith(_ticket, Lines(5)));
        _scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0));
        var (streamed, _) = RecordingSink(out var sink);

        await observer.ObserveAndCompleteAsync(_machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None, outputSink: sink);

        streamed.ShouldContain(l => l.Text.Contains("truncated"),
            customMessage: "the marker must be streamed so it persists even when bulk persist is skipped in streaming mode.");
        streamed.Single(l => l.Text.Contains("truncated")).IsStdErr.ShouldBeFalse(
            customMessage: "truncation is an informational notice, not a script error — must not surface as stderr/Error.");
    }

    [Fact]
    public async Task NoTruncation_EmitsNoGapMarker()
    {
        var observer = new HalibutScriptObserver(new ObserverSettings { MaxLogEntries = 100 });
        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>())).ReturnsAsync(CompleteWith(_ticket, Lines(3)));
        _scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        var result = await observer.ObserveAndCompleteAsync(_machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None);

        result.LogLines.ShouldNotContain(l => l.Contains("truncated"));
        result.LogLines.Count.ShouldBe(3);
    }
}

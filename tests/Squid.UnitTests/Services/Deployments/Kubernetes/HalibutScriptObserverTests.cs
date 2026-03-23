using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class HalibutScriptObserverTests
{
    private readonly HalibutScriptObserver _observer = new();
    private readonly Mock<IAsyncScriptService> _scriptClient = new();
    private readonly Machine _machine = new() { Name = "test-agent" };
    private readonly ScriptTicket _ticket = new("test-ticket");
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(30);

    // === Success ===

    [Fact]
    public async Task ObserveAndCompleteAsync_Success_ReturnsSuccessResult()
    {
        SetupImmediateComplete(exitCode: 0);

        var result = await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.ExitCode.ShouldBe(0);
    }

    // === Non-zero exit code ===

    [Fact]
    public async Task ObserveAndCompleteAsync_NonZeroExitCode_ReturnsFailed()
    {
        SetupImmediateComplete(exitCode: 42);

        var result = await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ExitCode.ShouldBe(42);
    }

    // === Multi-poll log collection ===

    [Fact]
    public async Task ObserveAndCompleteAsync_MultiplePolls_CollectsAllLogs()
    {
        var callCount = 0;

        _scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ReturnsAsync(_ticket);

        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount < 3)
                {
                    return new ScriptStatusResponse(
                        _ticket, ProcessState.Running, 0,
                        new List<ProcessOutput> { new(ProcessOutputSource.StdOut, $"log-{callCount}") },
                        callCount);
                }

                return new ScriptStatusResponse(
                    _ticket, ProcessState.Complete, 0,
                    new List<ProcessOutput> { new(ProcessOutputSource.StdOut, "log-final") },
                    callCount);
            });

        _scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        var result = await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.LogLines.ShouldContain("log-1");
        result.LogLines.ShouldContain("log-2");
        result.LogLines.ShouldContain("log-final");
        _scriptClient.Verify(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()), Times.Exactly(3));
    }

    // === Timeout ===

    [Fact]
    public async Task ObserveAndCompleteAsync_Timeout_ReturnsFailed_AndCallsCancel()
    {
        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Running, 0, new List<ProcessOutput>(), 0));

        _scriptClient.Setup(s => s.CancelScriptAsync(It.IsAny<CancelScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, ScriptExitCodes.Canceled, new List<ProcessOutput>(), 0));

        var shortTimeout = TimeSpan.FromMilliseconds(100);

        var result = await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, shortTimeout, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ExitCode.ShouldBe(ScriptExitCodes.Timeout);
        result.LogLines.ShouldNotBeEmpty();
        _scriptClient.Verify(s => s.CancelScriptAsync(It.IsAny<CancelScriptCommand>()), Times.Once);
    }

    [Fact]
    public async Task ObserveAndCompleteAsync_Timeout_IncludesCollectedLogs()
    {
        var callCount = 0;

        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return new ScriptStatusResponse(
                    _ticket, ProcessState.Running, 0,
                    new List<ProcessOutput> { new(ProcessOutputSource.StdOut, $"progress-{callCount}") },
                    callCount);
            });

        _scriptClient.Setup(s => s.CancelScriptAsync(It.IsAny<CancelScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, ScriptExitCodes.Canceled, new List<ProcessOutput>(), 0));

        var shortTimeout = TimeSpan.FromMilliseconds(100);

        var result = await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, shortTimeout, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.LogLines.Count.ShouldBeGreaterThan(1);
        result.LogLines.ShouldContain(l => l.StartsWith("progress-"));
        result.LogLines.Last().ShouldContain("timeout");
    }

    // === CancellationToken ===

    [Fact]
    public async Task ObserveAndCompleteAsync_CancellationRequested_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();

        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(() =>
            {
                cts.Cancel();
                return new ScriptStatusResponse(_ticket, ProcessState.Running, 0, new List<ProcessOutput>(), 0);
            });

        _scriptClient.Setup(s => s.CancelScriptAsync(It.IsAny<CancelScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, ScriptExitCodes.Canceled, new List<ProcessOutput>(), 0));

        await Should.ThrowAsync<OperationCanceledException>(
            () => _observer.ObserveAndCompleteAsync(_machine, _scriptClient.Object, _ticket, _timeout, cts.Token));
    }

    [Fact]
    public async Task ObserveAndCompleteAsync_Cancelled_CallsCancelScript()
    {
        using var cts = new CancellationTokenSource();

        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(() =>
            {
                cts.Cancel();
                return new ScriptStatusResponse(_ticket, ProcessState.Running, 0, new List<ProcessOutput>(), 0);
            });

        _scriptClient.Setup(s => s.CancelScriptAsync(It.IsAny<CancelScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, ScriptExitCodes.Canceled, new List<ProcessOutput>(), 0));

        await Should.ThrowAsync<OperationCanceledException>(
            () => _observer.ObserveAndCompleteAsync(_machine, _scriptClient.Object, _ticket, _timeout, cts.Token));

        _scriptClient.Verify(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()), Times.Once);
        _scriptClient.Verify(s => s.CancelScriptAsync(It.IsAny<CancelScriptCommand>()), Times.Once);
    }

    // === Masking ===

    [Fact]
    public async Task ObserveAndCompleteAsync_CompleteResponseLogs_AreMasked()
    {
        var masker = new Squid.Core.Services.DeploymentExecution.Lifecycle.SensitiveValueMasker(new[] { "my-secret-token" });

        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        _scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0,
                new List<ProcessOutput> { new(ProcessOutputSource.StdOut, "token=my-secret-token") }, 1));

        var result = await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None, masker);

        result.LogLines.ShouldContain(l => l.Contains("********"));
        result.LogLines.ShouldNotContain(l => l.Contains("my-secret-token"));
    }

    [Fact]
    public async Task ObserveAndCompleteAsync_GetStatusLogs_AreMasked()
    {
        var masker = new Squid.Core.Services.DeploymentExecution.Lifecycle.SensitiveValueMasker(new[] { "sensitive-password" });

        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0,
                new List<ProcessOutput> { new(ProcessOutputSource.StdOut, "pass=sensitive-password") }, 1));

        _scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 1));

        var result = await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None, masker);

        result.LogLines.ShouldContain(l => l.Contains("********"));
        result.LogLines.ShouldNotContain(l => l.Contains("sensitive-password"));
    }

    [Fact]
    public async Task ObserveAndCompleteAsync_NoMasker_LogsReturnedRaw()
    {
        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0,
                new List<ProcessOutput> { new(ProcessOutputSource.StdOut, "raw-output-data") }, 1));

        _scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 1));

        var result = await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None);

        result.LogLines.ShouldContain("raw-output-data");
    }

    [Fact]
    public async Task ObserveAndCompleteAsync_StderrLines_AreMasked()
    {
        var masker = new Squid.Core.Services.DeploymentExecution.Lifecycle.SensitiveValueMasker(new[] { "db-password" });

        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0,
                new List<ProcessOutput> { new(ProcessOutputSource.StdErr, "error: db-password leaked") }, 1));

        _scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 1, new List<ProcessOutput>(), 1));

        var result = await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None, masker);

        result.StderrLines.ShouldContain(l => l.Contains("********"));
        result.StderrLines.ShouldNotContain(l => l.Contains("db-password"));
    }

    // === Log Truncation ===

    [Fact]
    public async Task ObserveAndCompleteAsync_VerboseOutput_TruncatesOldestLogs()
    {
        var batchSize = 20_000;
        var callCount = 0;

        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                var logs = new List<ProcessOutput>();

                for (var i = 0; i < batchSize; i++)
                    logs.Add(new ProcessOutput(ProcessOutputSource.StdOut, $"line-{callCount}-{i}"));

                if (callCount >= 6)
                    return new ScriptStatusResponse(_ticket, ProcessState.Complete, 0, logs, callCount);

                return new ScriptStatusResponse(_ticket, ProcessState.Running, 0, logs, callCount);
            });

        _scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        var result = await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.LogLines.Count.ShouldBeLessThanOrEqualTo(HalibutScriptObserver.MaxLogEntries);
    }

    [Fact]
    public async Task ObserveAndCompleteAsync_NormalOutput_NoTruncation()
    {
        var callCount = 0;

        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                var logs = new List<ProcessOutput>
                {
                    new(ProcessOutputSource.StdOut, $"line-{callCount}")
                };

                return new ScriptStatusResponse(
                    _ticket, callCount >= 3 ? ProcessState.Complete : ProcessState.Running, 0, logs, callCount);
            });

        _scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        var result = await _observer.ObserveAndCompleteAsync(
            _machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.LogLines.Count.ShouldBe(3);
    }

    // === Configurable Timeout Default ===

    [Fact]
    public void ScriptTimeoutMinutes_DefaultValue_Is30()
    {
        var settings = new Squid.Core.Settings.Halibut.PollingSettings();

        settings.ScriptTimeoutMinutes.ShouldBe(30);
    }

    // === Helpers ===

    private void SetupImmediateComplete(int exitCode)
    {
        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, exitCode, new List<ProcessOutput>(), 0));

        _scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Complete, exitCode, new List<ProcessOutput>(), 0));
    }
}

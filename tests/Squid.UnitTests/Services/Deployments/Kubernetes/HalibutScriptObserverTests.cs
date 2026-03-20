using System;
using System.Collections.Generic;
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

    // === CancellationToken ===

    [Fact]
    public async Task ObserveAndCompleteAsync_CancellationRequested_ThrowsOperationCanceled()
    {
        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(_ticket, ProcessState.Running, 0, new List<ProcessOutput>(), 0));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(
            () => _observer.ObserveAndCompleteAsync(_machine, _scriptClient.Object, _ticket, _timeout, cts.Token));
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

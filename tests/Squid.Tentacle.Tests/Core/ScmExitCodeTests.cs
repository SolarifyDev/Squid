using System.Reflection;
using Microsoft.Extensions.Hosting;
using Moq;
using Shouldly;
using Squid.Tentacle.Core;
using Squid.Tentacle.Tests.Support;

namespace Squid.Tentacle.Tests.Core;

/// <summary>
/// Pins what the Windows SCM is told when the agent stops. A fatal agent exit must be a non-zero
/// service exit code (so `sc failure` restart fires); an operator stop must stay 0 (no restart loop).
/// The real SCM behaviour is proven by TentacleScmFatalExitRecoveryE2ETests (Windows only).
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class ScmExitCodeTests
{
    [Theory]
    [InlineData(1, false, 1)]     // fatal error, nobody asked to stop -> failure reported
    [InlineData(403, false, 403)] // permission-denied exit code is passed through
    [InlineData(0, false, 0)]     // clean early exit -> unchanged
    [InlineData(0, true, 0)]      // operator stop, clean
    [InlineData(1, true, 0)]      // failure while stopping must NOT trigger a restart against the operator's stop
    public void Resolve_MapsFatalToNonZero_AndOperatorStopToZero(int pipelineExit, bool stopRequested, int expected)
    {
        ScmExitCode.Resolve(pipelineExit, stopRequested).ShouldBe(expected);
    }

    [Fact]
    public async Task HostedService_FatalPipelineExit_ReportsNonZeroToScm()
    {
        var reported = new List<int>();
        var service = new TentacleScmHostedService(new[] { "no-such-command-scm-e2e" }, Mock.Of<IHostApplicationLifetime>(), reported.Add);

        await InvokeExecuteAsync(service, CancellationToken.None);

        service.LastExitCode.ShouldBe(1);
        reported.ShouldBe(new[] { 1 });
    }

    [Fact]
    public async Task HostedService_StopRequested_ReportsZeroToScm()
    {
        var reported = new List<int>();
        var service = new TentacleScmHostedService(new[] { "no-such-command-scm-e2e" }, Mock.Of<IHostApplicationLifetime>(), reported.Add);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await InvokeExecuteAsync(service, cts.Token);

        reported.ShouldBe(new[] { 0 });
    }

    [Fact]
    public async Task HostedService_ReportsExitCode_BeforeStoppingHost()
    {
        var order = new List<string>();
        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.Setup(l => l.StopApplication()).Callback(() => order.Add("stop"));
        var service = new TentacleScmHostedService(new[] { "no-such-command-scm-e2e" }, lifetime.Object, _ => order.Add("report"));

        await InvokeExecuteAsync(service, CancellationToken.None);

        order.ShouldBe(new[] { "report", "stop" });
    }

    private static Task InvokeExecuteAsync(TentacleScmHostedService service, CancellationToken ct)
    {
        var method = typeof(TentacleScmHostedService).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        return (Task)method.Invoke(service, new object[] { ct })!;
    }
}

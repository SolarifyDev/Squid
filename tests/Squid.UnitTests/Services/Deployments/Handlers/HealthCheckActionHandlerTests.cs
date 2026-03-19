using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Handlers;

public class HealthCheckActionHandlerTests
{
    [Fact]
    public void ActionType_IsHealthCheck()
    {
        var handler = CreateHandler(out _);

        handler.ActionType.ShouldBe(DeploymentActionType.HealthCheck);
    }

    [Fact]
    public void ExecutionScope_IsStepLevel()
    {
        var handler = CreateHandler(out _);

        handler.ExecutionScope.ShouldBe(ExecutionScope.StepLevel);
    }

    [Fact]
    public void CanHandle_HealthCheckAction_ReturnsTrue()
    {
        var handler = CreateHandler(out _);
        var action = new DeploymentActionDto { ActionType = "Squid.HealthCheck" };

        handler.CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_OtherAction_ReturnsFalse()
    {
        var handler = CreateHandler(out _);
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesRunScript" };

        handler.CanHandle(action).ShouldBeFalse();
    }

    // === Connection Test + SkipUnavailable ===

    [Fact]
    public async Task ExecuteStepLevelAsync_AllTargetsHealthy_NoneExcluded()
    {
        var handler = CreateHandler(out var events);
        var ctx = CreateContext(new[]
        {
            ("node-1", new HealthCheckResult(true, "ok")),
            ("node-2", new HealthCheckResult(true, "ok"))
        });

        await handler.ExecuteStepLevelAsync(ctx, CancellationToken.None);

        ctx.DeploymentContext.AllTargetsContext.Count.ShouldBe(2);
        ctx.DeploymentContext.AllTargetsContext.ShouldAllBe(tc => !tc.IsExcluded);
    }

    [Fact]
    public async Task ExecuteStepLevelAsync_OneUnhealthy_ExcludedFromContext()
    {
        var handler = CreateHandler(out _);
        var ctx = CreateContext(new[]
        {
            ("node-1", new HealthCheckResult(true, "ok")),
            ("node-2", new HealthCheckResult(false, "connection refused"))
        });

        await handler.ExecuteStepLevelAsync(ctx, CancellationToken.None);

        var node1 = ctx.DeploymentContext.AllTargetsContext.Single(tc => tc.Machine.Name == "node-1");
        var node2 = ctx.DeploymentContext.AllTargetsContext.Single(tc => tc.Machine.Name == "node-2");
        node1.IsExcluded.ShouldBeFalse();
        node2.IsExcluded.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteStepLevelAsync_AllUnhealthy_AllExcluded()
    {
        var handler = CreateHandler(out _);
        var ctx = CreateContext(new[]
        {
            ("node-1", new HealthCheckResult(false, "timeout")),
            ("node-2", new HealthCheckResult(false, "refused"))
        });

        await handler.ExecuteStepLevelAsync(ctx, CancellationToken.None);

        ctx.DeploymentContext.AllTargetsContext.ShouldAllBe(tc => tc.IsExcluded);
    }

    [Fact]
    public async Task ExecuteStepLevelAsync_HealthCheckerThrows_TreatedAsUnhealthy()
    {
        var handler = CreateHandler(out _);
        var throwingChecker = new Mock<IHealthCheckStrategy>();
        throwingChecker.Setup(h => h.CheckConnectivityAsync(It.IsAny<Machine>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("network error"));

        var ctx = CreateContextWithChecker("node-1", throwingChecker.Object);

        await handler.ExecuteStepLevelAsync(ctx, CancellationToken.None);

        ctx.DeploymentContext.AllTargetsContext.Single().IsExcluded.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteStepLevelAsync_NoHealthChecker_TreatedAsHealthy()
    {
        var handler = CreateHandler(out _);
        var ctx = CreateContextWithChecker("node-1", healthChecker: null);

        await handler.ExecuteStepLevelAsync(ctx, CancellationToken.None);

        ctx.DeploymentContext.AllTargetsContext.Single().IsExcluded.ShouldBeFalse();
        ctx.DeploymentContext.AllTargetsContext.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteStepLevelAsync_NoTargets_DoesNotThrow()
    {
        var handler = CreateHandler(out _);
        var ctx = new StepActionContext
        {
            Step = new DeploymentStepDto { Name = "HC" },
            Action = new DeploymentActionDto { Name = "Health Check" },
            StepDisplayOrder = 1,
            DeploymentContext = new DeploymentTaskContext
            {
                AllTargetsContext = new List<DeploymentTargetContext>()
            }
        };

        await Should.NotThrowAsync(() => handler.ExecuteStepLevelAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteStepLevelAsync_EmitsLifecycleEvents()
    {
        var handler = CreateHandler(out var events);
        var ctx = CreateContext(new[]
        {
            ("node-1", new HealthCheckResult(true, "ok")),
            ("node-2", new HealthCheckResult(false, "fail"))
        });

        await handler.ExecuteStepLevelAsync(ctx, CancellationToken.None);

        events.ShouldContain(e => e is HealthCheckStartingEvent);
        events.Count(e => e is HealthCheckTargetResultEvent).ShouldBe(2);
        events.ShouldContain(e => e is HealthCheckCompletedEvent);

        var completed = events.OfType<HealthCheckCompletedEvent>().Single();
        completed.Context.HealthCheckHealthyCount.ShouldBe(1);
        completed.Context.HealthCheckUnhealthyCount.ShouldBe(1);
    }

    // === FailDeployment error handling ===

    [Fact]
    public async Task ExecuteStepLevelAsync_FailDeployment_UnhealthyTarget_Throws()
    {
        var handler = CreateHandler(out _);
        var ctx = CreateContext(new[]
        {
            ("node-1", new HealthCheckResult(true, "ok")),
            ("node-2", new HealthCheckResult(false, "refused"))
        }, errorHandling: "TreatExceptionsAsErrors");

        await Should.ThrowAsync<DeploymentTargetException>(() => handler.ExecuteStepLevelAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteStepLevelAsync_FailDeployment_AllHealthy_DoesNotThrow()
    {
        var handler = CreateHandler(out _);
        var ctx = CreateContext(new[]
        {
            ("node-1", new HealthCheckResult(true, "ok")),
            ("node-2", new HealthCheckResult(true, "ok"))
        }, errorHandling: "TreatExceptionsAsErrors");

        await Should.NotThrowAsync(() => handler.ExecuteStepLevelAsync(ctx, CancellationToken.None));
    }

    // === ParseSettings ===

    [Theory]
    [InlineData(null, null, null, HealthCheckType.FullHealthCheck, HealthCheckErrorHandling.FailDeployment, false)]
    [InlineData("ConnectionTest", null, null, HealthCheckType.ConnectionTest, HealthCheckErrorHandling.FailDeployment, false)]
    [InlineData("FullHealthCheck", "TreatExceptionsAsWarnings", null, HealthCheckType.FullHealthCheck, HealthCheckErrorHandling.SkipUnavailable, false)]
    [InlineData("ConnectionTest", "TreatExceptionsAsErrors", "IncludeCheckedMachines", HealthCheckType.ConnectionTest, HealthCheckErrorHandling.FailDeployment, true)]
    [InlineData(null, null, "DoNotAlterMachines", HealthCheckType.FullHealthCheck, HealthCheckErrorHandling.FailDeployment, false)]
    public void ParseSettings_MapsPropertiesToSettings(string type, string errorHandling, string includeMachines, HealthCheckType expectedType, HealthCheckErrorHandling expectedError, bool expectedInclude)
    {
        var action = BuildAction(type, errorHandling, includeMachines);

        var settings = HealthCheckActionHandler.ParseSettings(action);

        settings.CheckType.ShouldBe(expectedType);
        settings.ErrorHandling.ShouldBe(expectedError);
        settings.IncludeNewTargets.ShouldBe(expectedInclude);
    }

    // === Helpers ===

    private static HealthCheckActionHandler CreateHandler(out List<DeploymentLifecycleEvent> capturedEvents)
    {
        var events = new List<DeploymentLifecycleEvent>();
        capturedEvents = events;

        var lifecycle = new Mock<IDeploymentLifecycle>();
        lifecycle.Setup(l => l.EmitAsync(It.IsAny<DeploymentLifecycleEvent>(), It.IsAny<CancellationToken>()))
            .Callback<DeploymentLifecycleEvent, CancellationToken>((e, _) => events.Add(e))
            .Returns(Task.CompletedTask);

        var targetFinder = new Mock<IDeploymentTargetFinder>();

        return new HealthCheckActionHandler(lifecycle.Object, targetFinder.Object);
    }

    private static DeploymentActionDto BuildAction(string checkType = null, string errorHandling = null, string includeMachines = null)
    {
        var properties = new List<DeploymentActionPropertyDto>();

        if (checkType != null)
            properties.Add(new DeploymentActionPropertyDto { PropertyName = "Squid.Action.HealthCheck.Type", PropertyValue = checkType });

        if (errorHandling != null)
            properties.Add(new DeploymentActionPropertyDto { PropertyName = "Squid.Action.HealthCheck.ErrorHandling", PropertyValue = errorHandling });

        if (includeMachines != null)
            properties.Add(new DeploymentActionPropertyDto { PropertyName = "Squid.Action.HealthCheck.IncludeMachinesInDeployment", PropertyValue = includeMachines });

        return new DeploymentActionDto { Name = "Health Check", ActionType = "Squid.HealthCheck", Properties = properties };
    }

    private static StepActionContext CreateContext((string MachineName, HealthCheckResult Result)[] targets, string errorHandling = "TreatExceptionsAsWarnings")
    {
        var allTargetsContext = new List<DeploymentTargetContext>();

        foreach (var (name, result) in targets)
        {
            var checker = new Mock<IHealthCheckStrategy>();
            checker.Setup(h => h.CheckConnectivityAsync(It.IsAny<Machine>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);

            var transport = new Mock<IDeploymentTransport>();
            transport.Setup(t => t.HealthChecker).Returns(checker.Object);

            allTargetsContext.Add(new DeploymentTargetContext
            {
                Machine = new Machine { Id = allTargetsContext.Count + 1, Name = name },
                Transport = transport.Object
            });
        }

        return new StepActionContext
        {
            Step = new DeploymentStepDto { Name = "Health Check Step" },
            Action = BuildAction(checkType: "ConnectionTest", errorHandling: errorHandling),
            StepDisplayOrder = 1,
            DeploymentContext = new DeploymentTaskContext
            {
                AllTargetsContext = allTargetsContext
            }
        };
    }

    private static StepActionContext CreateContextWithChecker(string machineName, IHealthCheckStrategy healthChecker)
    {
        var transport = new Mock<IDeploymentTransport>();
        transport.Setup(t => t.HealthChecker).Returns(healthChecker);

        var allTargetsContext = new List<DeploymentTargetContext>
        {
            new()
            {
                Machine = new Machine { Id = 1, Name = machineName },
                Transport = transport.Object
            }
        };

        return new StepActionContext
        {
            Step = new DeploymentStepDto { Name = "Health Check Step" },
            Action = BuildAction(checkType: "ConnectionTest", errorHandling: "TreatExceptionsAsWarnings"),
            StepDisplayOrder = 1,
            DeploymentContext = new DeploymentTaskContext
            {
                AllTargetsContext = allTargetsContext
            }
        };
    }
}

using Autofac;
using Microsoft.Extensions.Configuration;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.E2ETests.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.E2ETests.Deployments;

public class DeploymentPipelineFixture<TTestClass> : E2EFixtureBase<TTestClass>
{
    public CapturingExecutionStrategy ExecutionCapture { get; } = new();

    protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
    {
        builder.Register(ctx =>
            new CapturingTransportRegistry(
                ctx.Resolve<IEnumerable<IDeploymentTransport>>(),
                ExecutionCapture))
            .As<ITransportRegistry>()
            .InstancePerLifetimeScope();
    }

    // Wraps the real registry so each resolved transport uses the capturing strategy
    // while retaining real variable contribution and script wrapping.
    private sealed class CapturingTransportRegistry : ITransportRegistry
    {
        private readonly Dictionary<CommunicationStyle, IDeploymentTransport> _transports;
        private readonly CapturingExecutionStrategy _capture;

        public CapturingTransportRegistry(
            IEnumerable<IDeploymentTransport> transports,
            CapturingExecutionStrategy capture)
        {
            _transports = transports.ToDictionary(t => t.CommunicationStyle);
            _capture = capture;
        }

        public IDeploymentTransport Resolve(CommunicationStyle style)
            => _transports.TryGetValue(style, out var t)
                ? new StrategyCapturingTransport(t, _capture)
                : null;
    }

    private sealed class StrategyCapturingTransport : IDeploymentTransport
    {
        private readonly IDeploymentTransport _inner;

        public CommunicationStyle CommunicationStyle => _inner.CommunicationStyle;
        public IEndpointVariableContributor Variables => _inner.Variables;
        public IScriptContextWrapper ScriptWrapper => _inner.ScriptWrapper;
        public IExecutionStrategy Strategy { get; }
        public IHealthCheckStrategy HealthChecker => _inner.HealthChecker;
        public ExecutionLocation ExecutionLocation => _inner.ExecutionLocation;
        public ExecutionBackend ExecutionBackend => _inner.ExecutionBackend;
        public bool RequiresContextPreparationForPackagedPayload => _inner.RequiresContextPreparationForPackagedPayload;

        public StrategyCapturingTransport(IDeploymentTransport inner, IExecutionStrategy strategy)
        {
            _inner = inner;
            Strategy = strategy;
        }
    }
}

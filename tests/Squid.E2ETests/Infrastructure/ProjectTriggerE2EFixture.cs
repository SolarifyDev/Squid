using System.Linq.Expressions;
using System.Text.Json;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Squid.Core.Constants;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Jobs;
using Squid.Core.Services.ProjectTriggers;
using Squid.Core.Services.ProjectTriggers.Actions;
using Squid.E2ETests.Deployments;
using Squid.E2ETests.ProjectTriggers;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.E2ETests.Infrastructure;

public class ProjectTriggerE2EFixture : DeploymentPipelineFixture<ProjectTriggerE2ETests>
{
    public List<Func<IDeploymentTaskExecutor, Task>> Deliveries { get; } = [];

    protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
    {
        base.RegisterOverrides(builder, configuration);
        builder.RegisterType<HttpContextAccessor>().As<IHttpContextAccessor>().SingleInstance();
        builder.RegisterType<TestSource>().As<IProjectTriggerHandler>().InstancePerLifetimeScope();

        var jobs = Substitute.For<ISquidBackgroundJobClient>();
        jobs.Enqueue(Arg.Any<Expression<Func<IDeploymentTaskExecutor, Task>>>(), HangfireConstants.DefaultQueue)
            .Returns(call =>
            {
                Deliveries.Add(call.Arg<Expression<Func<IDeploymentTaskExecutor, Task>>>().Compile());
                return $"test-job-{Deliveries.Count}";
            });
        builder.RegisterInstance(jobs).As<ISquidBackgroundJobClient>();
    }

    // The fixture supplies facts directly so it exercises the common path independently of any scheduling rules.
    private sealed class TestSource(ITriggerReleasePackageResolver packages) : IProjectTriggerHandler
    {
        public string TriggerType => ProjectTriggerTypes.Scheduled;
        public IReadOnlyList<string> ActionTypes => [ProjectTriggerActions.DeployNewRelease];

        public Task<TriggerResourceScope> ValidateAsync(ProjectTriggerDefinition definition, CancellationToken cancellationToken)
        {
            using var config = JsonDocument.Parse(definition.ConfigurationJson);
            var channel = config.RootElement.GetProperty("channelId").GetInt32();
            var environment = config.RootElement.GetProperty("environmentId").GetInt32();

            return Task.FromResult(new TriggerResourceScope(channel, DestinationEnvironmentId: environment));
        }

        public async Task<TriggerEvaluation> EvaluateAsync(ProjectTriggerDefinition definition, TriggerObservation observation,
            string cursor, CancellationToken cancellationToken)
        {
            var resources = await ValidateAsync(definition, cancellationToken);
            var plan = new TriggerActionPlan { ChannelId = resources.ChannelId, EnvironmentId = resources.DestinationEnvironmentId };
            await packages.PrepareAsync(definition, plan, cancellationToken);
            var occurrence = new TriggerOccurrence("fixture-occurrence", "{}", plan);

            return new TriggerEvaluation("observed", [occurrence], "Test occurrence received.");
        }
    }
}

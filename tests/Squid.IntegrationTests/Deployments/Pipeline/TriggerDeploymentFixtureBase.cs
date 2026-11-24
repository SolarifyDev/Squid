using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Release;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Constants;
using Squid.Message.Events.Deployments.Deployment;

namespace Squid.IntegrationTests.Deployments.Pipeline;

public abstract class TriggerDeploymentFixtureBase : DeploymentFixtureBase
{
    protected async Task<CreateDeploymentCommand> SeedReleaseAsync()
    {
        var command = new CreateDeploymentCommand { Name = "trigger handoff" };

        await Run<IRepository, IUnitOfWork, IReleaseService>(async (repository, unitOfWork, releases) =>
        {
            var data = new TestDataBuilder(repository, unitOfWork);
            var environment = await data.CreateEnvironmentAsync();
            var lifecycle = await data.CreateLifecycleAsync();
            await data.CreateLifecyclePhaseAsync(lifecycle.Id, environment.Id);

            var group = await data.CreateProjectGroupAsync();
            var variables = await data.CreateVariableSetAsync();
            var process = await data.CreateDeploymentProcessAsync();
            var project = await data.CreateProjectAsync(variables.Id, process.Id, group.Id, lifecycle.Id);
            await data.UpdateVariableSetOwnerAsync(variables, project.Id);
            var channel = await data.CreateChannelAsync(project.Id, lifecycle.Id);
            var step = await data.CreateDeploymentStepAsync(process.Id, 1, "Local script");
            await data.CreateStepPropertiesAsync(step.Id, (SpecialVariables.Step.RunOnServer, "true"));
            var action = await data.CreateDeploymentActionAsync(step.Id, 1, "Run script");
            await data.CreateActionPropertiesAsync(action.Id, (SpecialVariables.Action.ScriptBody, "echo deployed"),
                (SpecialVariables.Action.ScriptSyntax, "Bash"));

            var release = await releases.CreateReleaseAsync(new CreateReleaseCommand
                { ProjectId = project.Id, ChannelId = channel.Id, Version = "1.0.0" });

            command.ReleaseId = release.Release.Id;
            command.EnvironmentId = environment.Id;
        });

        return command;
    }

    protected async Task<DeploymentCreatedEvent> CreateTriggerDeploymentAsync()
    {
        var command = await SeedReleaseAsync();
        DeploymentCreatedEvent created = null;

        await Run<SquidDbContext, IDeploymentService>(async (db, deployments) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            created = await deployments.CreateDeploymentForTriggerAsync(command);
            await tx.CommitAsync();
        });

        return created;
    }
}

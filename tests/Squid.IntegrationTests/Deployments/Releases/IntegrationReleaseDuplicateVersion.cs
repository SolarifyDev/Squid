using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.Releases.Exceptions;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Release;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;

namespace Squid.IntegrationTests.Deployments.Releases;

public class IntegrationReleaseDuplicateVersion : TestBase
{
    public IntegrationReleaseDuplicateVersion()
        : base("ReleaseDuplicateVersion", "squid_it_release_duplicate_version")
    {
    }

    [Fact]
    public async Task CreateRelease_DuplicateVersionSameProjectAndChannel_Throws()
    {
        int projectId = 0, channelId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var lifecycle = await builder.CreateLifecycleAsync().ConfigureAwait(false);
            var env = await builder.CreateEnvironmentAsync("Development").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, env.Id, "Dev Phase").ConfigureAwait(false);

            var group = await builder.CreateProjectGroupAsync().ConfigureAwait(false);
            var varSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(varSet.Id, 0, group.Id, lifecycle.Id).ConfigureAwait(false);
            projectId = project.Id;

            var channel = await builder.CreateChannelAsync(project.Id, lifecycle.Id).ConfigureAwait(false);
            channelId = channel.Id;

            await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IReleaseService>(async service =>
        {
            var command = new CreateReleaseCommand
            {
                Version = "1.0.0",
                ProjectId = projectId,
                ChannelId = channelId
            };

            var ex = await Should.ThrowAsync<ReleaseDuplicateVersionException>(
                () => service.CreateReleaseAsync(command, CancellationToken.None)).ConfigureAwait(false);

            ex.ProjectId.ShouldBe(projectId);
            ex.ChannelId.ShouldBe(channelId);
            ex.Version.ShouldBe("1.0.0");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetReleaseByVersion_SameVersionDifferentChannel_ReturnsNull()
    {
        await Run<IRepository, IUnitOfWork, IReleaseDataProvider>(async (repository, unitOfWork, releaseDataProvider) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var lifecycle = await builder.CreateLifecycleAsync().ConfigureAwait(false);
            var env = await builder.CreateEnvironmentAsync("Development").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, env.Id, "Dev Phase").ConfigureAwait(false);

            var group = await builder.CreateProjectGroupAsync().ConfigureAwait(false);
            var varSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(varSet.Id, 0, group.Id, lifecycle.Id).ConfigureAwait(false);

            var channel1 = await builder.CreateChannelAsync(project.Id, lifecycle.Id).ConfigureAwait(false);
            var channel2 = await builder.CreateChannelAsync(project.Id, lifecycle.Id).ConfigureAwait(false);

            await builder.CreateReleaseAsync(project.Id, channel1.Id, "1.0.0").ConfigureAwait(false);

            var result = await releaseDataProvider.GetReleaseByVersionAsync(project.Id, channel2.Id, "1.0.0").ConfigureAwait(false);

            result.ShouldBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetReleaseByVersion_SameVersionDifferentProject_ReturnsNull()
    {
        await Run<IRepository, IUnitOfWork, IReleaseDataProvider>(async (repository, unitOfWork, releaseDataProvider) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var lifecycle = await builder.CreateLifecycleAsync().ConfigureAwait(false);
            var env = await builder.CreateEnvironmentAsync("Development").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, env.Id, "Dev Phase").ConfigureAwait(false);

            var group = await builder.CreateProjectGroupAsync().ConfigureAwait(false);

            var varSet1 = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project1 = await builder.CreateProjectAsync(varSet1.Id, 0, group.Id, lifecycle.Id, "Project A").ConfigureAwait(false);

            var varSet2 = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project2 = await builder.CreateProjectAsync(varSet2.Id, 0, group.Id, lifecycle.Id, "Project B").ConfigureAwait(false);

            var channel1 = await builder.CreateChannelAsync(project1.Id, lifecycle.Id).ConfigureAwait(false);
            var channel2 = await builder.CreateChannelAsync(project2.Id, lifecycle.Id).ConfigureAwait(false);

            await builder.CreateReleaseAsync(project1.Id, channel1.Id, "1.0.0").ConfigureAwait(false);

            var result = await releaseDataProvider.GetReleaseByVersionAsync(project2.Id, channel2.Id, "1.0.0").ConfigureAwait(false);

            result.ShouldBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task DbUniqueIndex_DuplicateDirectInsert_ThrowsDbException()
    {
        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var lifecycle = await builder.CreateLifecycleAsync().ConfigureAwait(false);
            var env = await builder.CreateEnvironmentAsync("Development").ConfigureAwait(false);
            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, env.Id, "Dev Phase").ConfigureAwait(false);

            var group = await builder.CreateProjectGroupAsync().ConfigureAwait(false);
            var varSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(varSet.Id, 0, group.Id, lifecycle.Id).ConfigureAwait(false);
            var channel = await builder.CreateChannelAsync(project.Id, lifecycle.Id).ConfigureAwait(false);

            await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            await Should.ThrowAsync<Exception>(async () =>
            {
                await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}

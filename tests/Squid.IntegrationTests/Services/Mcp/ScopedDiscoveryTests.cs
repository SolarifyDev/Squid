using System.Net;
using Mediator.Net.Context;
using Mediator.Net.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Squid.Core.Middlewares.Logging;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Teams;
using Squid.Message.Enums;
using Squid.Message.Requests.Mcp;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Services.Mcp;

public class ScopedDiscoveryTests : TestBase
{
    private const string ResourceNotFoundOrForbidden = "Resource not found or inaccessible.";
    private const int SpaceA = 11;
    private const int SpaceB = 12;
    private const int SpaceC = 13;
    private const int ProjectGroupA = 111;
    private const int ProjectGroupB = 112;
    private const int ProjectGroupC = 113;
    private const int ProjectA = 1011;
    private const int ProjectA2 = 1012;
    private const int ProjectB = 1021;
    private const int ProjectC = 1031;

    public ScopedDiscoveryTests()
        : base("ScopedDiscovery", "squid_it_mcp_scoped_discovery")
    {
    }

    [Fact]
    public async Task Spaces_ShowOnlySpacesWithVisibleProjects()
    {
        const int userId = 1101;
        await SeedSpacesAndProjectsAsync();
        await AddGrantsAsync(
            userId,
            new GrantSeed(SpaceA, "SpaceOneProjects", Permission.ProjectView, SpaceA, [ProjectA], [], []),
            new GrantSeed(SpaceB, "SpaceTwoProjects", Permission.ProjectView, SpaceB, [ProjectB], [], [])).ConfigureAwait(false);

        var response = await RequestAsAsync<ListVisibleSpacesRequest, ListVisibleSpacesResponse>(
            userId,
            new ListVisibleSpacesRequest { PageSize = 20 }).ConfigureAwait(false);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.Items.Select(item => item.Id).ShouldBe([SpaceA, SpaceB]);
        response.Data.Items.Select(item => item.Name).ShouldBe(["Alpha", "Beta"]);
        response.Data.NextPosition.ShouldBeNull();
    }

    [Fact]
    public async Task Projects_FilterBeforePage()
    {
        const int userId = 1102;
        await SeedProjectKeywordGraphAsync().ConfigureAwait(false);
        await AddGrantsAsync(
            userId,
            new GrantSeed(SpaceA, "KeywordProjects", Permission.ProjectView, SpaceA, [1012, 1013, 1014, 1015], [], [ProjectGroupA])).ConfigureAwait(false);

        var first = await RequestAsAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
            userId,
            new ListVisibleProjectsRequest { SpaceId = SpaceA, Keyword = "alpha", PageSize = 2 }).ConfigureAwait(false);

        first.Code.ShouldBe(HttpStatusCode.OK);
        first.Data.Items.Select(item => item.Id).ShouldBe([1013, 1014]);
        first.Data.Items.Select(item => item.Name).ShouldBe(["Alpha shared", "Alpha shared"]);
        first.Data.NextPosition.ShouldBe(new ScopedReadPosition(1014, null));

        var second = await RequestAsAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
            userId,
            new ListVisibleProjectsRequest { SpaceId = SpaceA, Keyword = "alpha", PageSize = 2, After = first.Data.NextPosition }).ConfigureAwait(false);

        second.Data.Items.Select(item => item.Id).ShouldBe([1015]);
        second.Data.NextPosition.ShouldBeNull();
    }

    [Fact]
    public async Task Projects_AuthorizedKeywordMiss_ReturnsEmptyPage()
    {
        const int userId = 1103;
        await SeedProjectKeywordGraphAsync().ConfigureAwait(false);
        await AddGrantsAsync(
            userId,
            new GrantSeed(SpaceA, "EmptyKeywordProjects", Permission.ProjectView, SpaceA, [1013], [], [ProjectGroupA])).ConfigureAwait(false);

        var response = await RequestAsAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
            userId,
            new ListVisibleProjectsRequest { SpaceId = SpaceA, Keyword = "not-present", PageSize = 20 }).ConfigureAwait(false);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.Items.ShouldBeEmpty();
        response.Data.NextPosition.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Projects_RestrictedGrant_HidesSpacesWithoutVisibleProjects(bool restrictByProject)
    {
        const int userId = 1110;
        await SeedSpacesAndProjectsAsync().ConfigureAwait(false);
        await AddGrantsAsync(
            userId,
            new GrantSeed(SpaceA, "RestrictedAcrossSpaces", Permission.ProjectView, null,
                restrictByProject ? [ProjectA] : [], [], restrictByProject ? [] : [ProjectGroupA]))
            .ConfigureAwait(false);

        var spaces = await RequestAsAsync<ListVisibleSpacesRequest, ListVisibleSpacesResponse>(
            userId, new ListVisibleSpacesRequest()).ConfigureAwait(false);
        var hidden = await RequestAsAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
            userId, new ListVisibleProjectsRequest { SpaceId = SpaceB }).ConfigureAwait(false);
        var missing = await RequestAsAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
            userId, new ListVisibleProjectsRequest { SpaceId = 999 }).ConfigureAwait(false);

        spaces.Data.Items.Select(item => item.Id).ShouldBe([SpaceA]);
        AssertNotFound(missing);
        AssertNotFound(hidden);
    }

    [Fact]
    public async Task Projects_UnrestrictedGrant_AllowsEmptySpace()
    {
        const int userId = 1111;
        await Run<SquidDbContext>(async context =>
        {
            context.Set<Space>().Add(ProjectEntityFactory.Space(SpaceA, "Alpha"));
            await context.SaveChangesAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        await AddGrantsAsync(userId, new GrantSeed(SpaceA, "AllSpaceProjects", Permission.ProjectView, SpaceA, [], [], []))
            .ConfigureAwait(false);

        var response = await RequestAsAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
            userId, new ListVisibleProjectsRequest { SpaceId = SpaceA }).ConfigureAwait(false);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.Items.ShouldBeEmpty();
        response.Data.NextPosition.ShouldBeNull();
    }

    [Theory]
    [InlineData(ProjectGroupB)]
    [InlineData(999)]
    public async Task Spaces_IgnoreProjectsWithInvalidGroup(int projectGroupId)
    {
        const int userId = 1112;
        await SeedBrokenProjectAsync(SpaceA, projectGroupId, spaceExists: true).ConfigureAwait(false);
        await AddGrantsAsync(userId, new GrantSeed(SpaceA, "BrokenProjectSpace", Permission.ProjectView, null, [], [], []))
            .ConfigureAwait(false);

        var response = await RequestAsAsync<ListVisibleSpacesRequest, ListVisibleSpacesResponse>(
            userId, new ListVisibleSpacesRequest()).ConfigureAwait(false);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ProjectGroupB)]
    [InlineData(999)]
    public async Task Projects_IgnoreProjectsWithInvalidGroup(int projectGroupId)
    {
        const int userId = 1113;
        await SeedBrokenProjectAsync(SpaceA, projectGroupId, spaceExists: true).ConfigureAwait(false);
        await AddGrantsAsync(userId, new GrantSeed(SpaceA, "BrokenProjectList", Permission.ProjectView, SpaceA, [], [], []))
            .ConfigureAwait(false);

        var response = await RequestAsAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
            userId, new ListVisibleProjectsRequest { SpaceId = SpaceA }).ConfigureAwait(false);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(SpaceA, ProjectGroupB, true)]
    [InlineData(SpaceA, 999, true)]
    [InlineData(999, ProjectGroupA, false)]
    public async Task EnvironmentsAndReleases_RejectProjectsWithBrokenRelationships(int spaceId, int projectGroupId,
        bool spaceExists)
    {
        const int userId = 1114;
        await SeedBrokenProjectAsync(spaceId, projectGroupId, spaceExists).ConfigureAwait(false);
        await AddGrantsAsync(
            userId,
            new GrantSeed(SpaceA, "BrokenEnvironmentProject", Permission.EnvironmentView, null, [ProjectA], [], []),
            new GrantSeed(SpaceA, "BrokenReleaseProject", Permission.ReleaseView, null, [ProjectA], [], []))
            .ConfigureAwait(false);

        var environments = await RequestAsAsync<ListVisibleEnvironmentsRequest, ListVisibleEnvironmentsResponse>(
            userId, new ListVisibleEnvironmentsRequest { SpaceId = spaceId, ProjectId = ProjectA }).ConfigureAwait(false);
        var releases = await RequestAsAsync<ListVisibleReleasesRequest, ListVisibleReleasesResponse>(
            userId, new ListVisibleReleasesRequest { SpaceId = spaceId, ProjectId = ProjectA }).ConfigureAwait(false);

        AssertNotFound(environments);
        AssertNotFound(releases);
    }

    [Fact]
    public async Task Environments_UseProjectAndEnvironmentFromSameGrant()
    {
        const int userId = 1104;
        await SeedEnvironmentGraphAsync().ConfigureAwait(false);
        await AddGrantsAsync(
            userId,
            new GrantSeed(SpaceA, "ProjectAEnvironmentX", Permission.EnvironmentView, SpaceA, [1011], [2011], [ProjectGroupA]),
            new GrantSeed(SpaceA, "ProjectBEnvironmentY", Permission.EnvironmentView, SpaceA, [1012], [2012], [ProjectGroupA])).ConfigureAwait(false);

        var projectA = await RequestAsAsync<ListVisibleEnvironmentsRequest, ListVisibleEnvironmentsResponse>(
            userId,
            new ListVisibleEnvironmentsRequest { SpaceId = SpaceA, ProjectId = 1011, PageSize = 20 }).ConfigureAwait(false);
        var projectB = await RequestAsAsync<ListVisibleEnvironmentsRequest, ListVisibleEnvironmentsResponse>(
            userId,
            new ListVisibleEnvironmentsRequest { SpaceId = SpaceA, ProjectId = 1012, PageSize = 20 }).ConfigureAwait(false);

        projectA.Data.Items.ShouldBe([new EnvironmentReadItem(2011, "Development")]);
        projectB.Data.Items.ShouldBe([new EnvironmentReadItem(2012, "Production")]);
    }

    [Fact]
    public async Task Releases_RequireMatchingSpaceAndProject()
    {
        const int userId = 1105;
        var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);

        await SeedReleaseGraphAsync().ConfigureAwait(false);
        await SeedReleasesAsync(
            (3011, SpaceA, 1011, "1.0.0", t2),
            (3012, SpaceA, 1011, "2.0.0", t3),
            (3013, SpaceB, ProjectB, "9.0.0", t3),
            (3014, SpaceB, 1011, "broken-space", t3),
            (3015, SpaceA, 1013, "other-project", t3)).ConfigureAwait(false);
        await AddGrantsAsync(
            userId,
            new GrantSeed(SpaceA, "ReleaseProjects", Permission.ReleaseView, SpaceA, [1011], [], [ProjectGroupA])).ConfigureAwait(false);

        var response = await RequestAsAsync<ListVisibleReleasesRequest, ListVisibleReleasesResponse>(
            userId,
            new ListVisibleReleasesRequest { SpaceId = SpaceA, ProjectId = 1011, PageSize = 20 }).ConfigureAwait(false);

        response.Data.Items.Select(item => item.Id).ShouldBe([3012, 3011]);
        response.Data.Items.Select(item => item.Version).ShouldBe(["2.0.0", "1.0.0"]);

        var crossSpace = await RequestAsAsync<ListVisibleReleasesRequest, ListVisibleReleasesResponse>(
            userId,
            new ListVisibleReleasesRequest { SpaceId = SpaceA, ProjectId = ProjectB, PageSize = 20 }).ConfigureAwait(false);

        AssertNotFound(crossSpace);
    }

    [Fact]
    public async Task Paging_UsesCurrentPermissionsOnEveryPage()
    {
        const int userId = 1106;
        await SeedPagingProjectsAsync().ConfigureAwait(false);
        var scopedRoleIds = await AddGrantsAsync(
            userId,
            new GrantSeed(SpaceA, "PagedProjects", Permission.ProjectView, SpaceA, [1011, 1012], [], [ProjectGroupA])).ConfigureAwait(false);

        var first = await RequestAsAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
            userId,
            new ListVisibleProjectsRequest { SpaceId = SpaceA, PageSize = 1 }).ConfigureAwait(false);
        first.Data.Items.Select(item => item.Id).ShouldBe([1011]);
        first.Data.NextPosition.ShouldBe(new ScopedReadPosition(1011, null));

        await ReplaceProjectScopeAsync(scopedRoleIds[0], 1013, 1014).ConfigureAwait(false);

        var second = await RequestAsAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
            userId,
            new ListVisibleProjectsRequest { SpaceId = SpaceA, PageSize = 1, After = first.Data.NextPosition }).ConfigureAwait(false);
        second.Data.Items.Select(item => item.Id).ShouldBe([1013]);

        await ReplaceProjectScopeAsync(scopedRoleIds[0], 1014).ConfigureAwait(false);

        var third = await RequestAsAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
            userId,
            new ListVisibleProjectsRequest { SpaceId = SpaceA, PageSize = 1, After = second.Data.NextPosition }).ConfigureAwait(false);
        third.Data.Items.Select(item => item.Id).ShouldBe([1014]);
        third.Data.NextPosition.ShouldBeNull();
    }

    [Fact]
    public async Task Parents_MissingOrForbidden_ReturnSameNotFound()
    {
        const int userId = 1107;
        await SeedSpacesAndProjectsAsync().ConfigureAwait(false);
        await AddGrantsAsync(
            userId,
            new GrantSeed(SpaceA, "VisibleProjectOnly", Permission.ProjectView, SpaceA, [ProjectA], [], []),
            new GrantSeed(SpaceA, "VisibleEnvironmentProjectOnly", Permission.EnvironmentView, SpaceA, [ProjectA], [2011], []),
            new GrantSeed(SpaceA, "VisibleReleaseProjectOnly", Permission.ReleaseView, SpaceA, [ProjectA], [], [])).ConfigureAwait(false);

        var missingProjects = await RequestAsAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
            userId,
            new ListVisibleProjectsRequest { SpaceId = 999, PageSize = 20 }).ConfigureAwait(false);
        var forbiddenProjects = await RequestAsAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
            userId,
            new ListVisibleProjectsRequest { SpaceId = SpaceB, PageSize = 20 }).ConfigureAwait(false);
        var crossSpaceEnvironment = await RequestAsAsync<ListVisibleEnvironmentsRequest, ListVisibleEnvironmentsResponse>(
            userId,
            new ListVisibleEnvironmentsRequest { SpaceId = SpaceA, ProjectId = ProjectB, PageSize = 20 }).ConfigureAwait(false);
        var crossSpaceRelease = await RequestAsAsync<ListVisibleReleasesRequest, ListVisibleReleasesResponse>(
            userId,
            new ListVisibleReleasesRequest { SpaceId = SpaceA, ProjectId = ProjectB, PageSize = 20 }).ConfigureAwait(false);

        AssertNotFound(missingProjects);
        AssertNotFound(forbiddenProjects);
        AssertNotFound(crossSpaceEnvironment);
        AssertNotFound(crossSpaceRelease);
        forbiddenProjects.Msg.ShouldBe(missingProjects.Msg);
    }

    [Fact]
    public async Task ScopedQueries_IgnoreSpaceHeader()
    {
        const int userId = 1108;
        await SeedSpacesAndProjectsAsync().ConfigureAwait(false);
        await AddGrantsAsync(
            userId,
            new GrantSeed(SpaceA, "HeaderIndependent", Permission.ProjectView, SpaceA, [ProjectA], [], [])).ConfigureAwait(false);
        var accessor = new MutableHttpContextAccessor();

        accessor.SetHeader(null);
        var withoutHeader = await RequestAsAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
            userId,
            new ListVisibleProjectsRequest { SpaceId = SpaceA, PageSize = 20 },
            accessor).ConfigureAwait(false);

        accessor.SetHeader(SpaceA.ToString());
        var matchingHeader = await RequestAsAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
            userId,
            new ListVisibleProjectsRequest { SpaceId = SpaceA, PageSize = 20 },
            accessor).ConfigureAwait(false);

        accessor.SetHeader(SpaceB.ToString());
        var conflictingHeader = await RequestAsAsync<ListVisibleProjectsRequest, ListVisibleProjectsResponse>(
            userId,
            new ListVisibleProjectsRequest { SpaceId = SpaceA, PageSize = 20 },
            accessor).ConfigureAwait(false);

        withoutHeader.Data.Items.Select(item => item.Id).ShouldBe([ProjectA]);
        matchingHeader.Data.Items.Select(item => item.Id).ShouldBe([ProjectA]);
        conflictingHeader.Data.Items.Select(item => item.Id).ShouldBe([ProjectA]);
    }

    [Fact]
    public async Task ScopedQueries_LoggerOmitsKeywordAndResponseContent()
    {
        const string keyword = "unique-keyword-987";
        const string responseName = "unique-response-project";
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var context = new Moq.Mock<IContext<IMessage>>();
        context.SetupGet(item => item.Message).Returns(new ListVisibleProjectsRequest
        {
            SpaceId = 1,
            Keyword = keyword,
            PageSize = 20
        });
        context.SetupGet(item => item.Result).Returns(new ListVisibleProjectsResponse
        {
            Data = new ScopedReadPage<ProjectReadItem>(
                [new ProjectReadItem(1, responseName, "response-slug")],
                null)
        });
        var specification = new LoggerSpecification<IContext<IMessage>>(logger);

        await specification.BeforeExecute(context.Object, CancellationToken.None).ConfigureAwait(false);
        await specification.AfterExecute(context.Object, CancellationToken.None).ConfigureAwait(false);

        var rendered = string.Join('\n', sink.Events.Select(item => item.RenderMessage()));
        rendered.ShouldNotContain(keyword);
        rendered.ShouldNotContain(responseName);
        rendered.ShouldNotContain("response-slug");
        rendered.ShouldContain(nameof(ListVisibleProjectsRequest));
    }

    [Fact]
    public async Task ScopedQueries_WithoutCurrentUser_FailClosed()
    {
        var exception = await Should.ThrowAsync<PermissionDeniedException>(() =>
            Run<IMediator, ListVisibleSpacesResponse>(async mediator =>
                await mediator.RequestAsync<ListVisibleSpacesRequest, ListVisibleSpacesResponse>(
                    new ListVisibleSpacesRequest { PageSize = 20 }).ConfigureAwait(false),
                builder => builder.RegisterInstance<ICurrentUser>(new FixedCurrentUser(null)).SingleInstance()));

        exception.Message.ShouldContain("user identity not resolved");
    }

    private async Task<TResponse> RequestAsAsync<TRequest, TResponse>(
        int userId,
        TRequest request,
        IHttpContextAccessor accessor = null)
        where TRequest : class, IRequest
        where TResponse : class, IResponse
    {
        return await Run<IMediator, TResponse>(
            mediator => mediator.RequestAsync<TRequest, TResponse>(request),
            builder =>
            {
                builder.RegisterInstance<ICurrentUser>(new FixedCurrentUser(userId)).SingleInstance();

                if (accessor != null)
                    builder.RegisterInstance(accessor).As<IHttpContextAccessor>().SingleInstance();
            }).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<int>> AddGrantsAsync(int userId, params GrantSeed[] grants)
    {
        var scopedRoleIds = new List<int>();

        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            foreach (var grant in grants)
            {
                var team = new Team
                {
                    Name = $"{grant.RoleName} Team {Guid.NewGuid():N}",
                    SpaceId = grant.TeamSpaceId,
                    IsBuiltIn = false
                };
                await teamProvider.AddAsync(team).ConfigureAwait(false);
                await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = userId }).ConfigureAwait(false);

                var role = new UserRole
                {
                    Name = $"{grant.RoleName} Role {Guid.NewGuid():N}",
                    IsBuiltIn = false
                };
                await roleProvider.AddAsync(role).ConfigureAwait(false);
                await roleProvider.SetPermissionsAsync(role.Id, [grant.Permission.ToString()]).ConfigureAwait(false);

                var scopedRole = new ScopedUserRole
                {
                    TeamId = team.Id,
                    UserRoleId = role.Id,
                    SpaceId = grant.RoleSpaceId
                };
                await scopedProvider.AddAsync(scopedRole).ConfigureAwait(false);
                await scopedProvider.SetProjectScopeAsync(scopedRole.Id, grant.ProjectIds.ToList()).ConfigureAwait(false);
                await scopedProvider.SetEnvironmentScopeAsync(scopedRole.Id, grant.EnvironmentIds.ToList()).ConfigureAwait(false);
                await scopedProvider.SetProjectGroupScopeAsync(scopedRole.Id, grant.ProjectGroupIds.ToList()).ConfigureAwait(false);

                scopedRoleIds.Add(scopedRole.Id);
            }
        }).ConfigureAwait(false);

        return scopedRoleIds;
    }

    private Task ReplaceProjectScopeAsync(int scopedRoleId, params int[] projectIds)
    {
        return Run<IScopedUserRoleDataProvider>(provider =>
            provider.SetProjectScopeAsync(scopedRoleId, projectIds.ToList()));
    }

    private Task SeedSpacesAndProjectsAsync()
    {
        return Run<SquidDbContext>(async context =>
        {
            context.Set<Space>().AddRange(
                ProjectEntityFactory.Space(SpaceA, "Alpha"),
                ProjectEntityFactory.Space(SpaceB, "Beta"),
                ProjectEntityFactory.Space(SpaceC, "Gamma"));
            context.Set<ProjectGroup>().AddRange(
                ProjectEntityFactory.ProjectGroup(ProjectGroupA, SpaceA, "Alpha Group"),
                ProjectEntityFactory.ProjectGroup(ProjectGroupB, SpaceB, "Beta Group"),
                ProjectEntityFactory.ProjectGroup(ProjectGroupC, SpaceC, "Gamma Group"));
            context.Set<Project>().AddRange(
                ProjectEntityFactory.Project(ProjectA, SpaceA, ProjectGroupA, "Alpha One", "alpha-one"),
                ProjectEntityFactory.Project(ProjectA2, SpaceA, ProjectGroupA, "Alpha Two", "alpha-two"),
                ProjectEntityFactory.Project(ProjectB, SpaceB, ProjectGroupB, "Beta One", "beta-one"),
                ProjectEntityFactory.Project(ProjectC, SpaceC, ProjectGroupC, "Gamma One", "gamma-one"));
            context.Set<Environment>().AddRange(
                ProjectEntityFactory.Environment(2011, SpaceA, "Development", "development"),
                ProjectEntityFactory.Environment(2012, SpaceA, "Production", "production"));

            await context.SaveChangesAsync().ConfigureAwait(false);
        });
    }

    private Task SeedBrokenProjectAsync(int spaceId, int projectGroupId, bool spaceExists)
    {
        return Run<SquidDbContext>(async context =>
        {
            if (spaceExists)
            {
                context.Set<Space>().Add(ProjectEntityFactory.Space(spaceId, "Alpha"));
                context.Set<Space>().Add(ProjectEntityFactory.Space(SpaceB, "Beta"));
                context.Set<ProjectGroup>().Add(ProjectEntityFactory.ProjectGroup(ProjectGroupB, SpaceB, "Beta Group"));
            }
            else
            {
                context.Set<ProjectGroup>().Add(ProjectEntityFactory.ProjectGroup(projectGroupId, spaceId, "Orphan Group"));
            }

            context.Set<Project>().Add(ProjectEntityFactory.Project(ProjectA, spaceId, projectGroupId, "Broken", "broken"));
            await context.SaveChangesAsync().ConfigureAwait(false);
        });
    }

    private Task SeedProjectKeywordGraphAsync()
    {
        return Run<SquidDbContext>(async context =>
        {
            context.Set<Space>().Add(ProjectEntityFactory.Space(SpaceA, "Alpha"));
            context.Set<ProjectGroup>().Add(ProjectEntityFactory.ProjectGroup(ProjectGroupA, SpaceA, "Alpha Group"));
            context.Set<Project>().AddRange(
                ProjectEntityFactory.Project(1011, SpaceA, ProjectGroupA, "Alpha forbidden first", "alpha-forbidden-first"),
                ProjectEntityFactory.Project(1012, SpaceA, ProjectGroupA, "Other authorized", "other-authorized"),
                ProjectEntityFactory.Project(1013, SpaceA, ProjectGroupA, "Alpha shared", "alpha-shared-a"),
                ProjectEntityFactory.Project(1014, SpaceA, ProjectGroupA, "Alpha shared", "alpha-shared-b"),
                ProjectEntityFactory.Project(1015, SpaceA, ProjectGroupA, "Alpha authorized third", "alpha-authorized-third"),
                ProjectEntityFactory.Project(1016, SpaceA, ProjectGroupA, "Alpha forbidden second", "alpha-forbidden-second"));

            await context.SaveChangesAsync().ConfigureAwait(false);
        });
    }

    private Task SeedEnvironmentGraphAsync()
    {
        return Run<SquidDbContext>(async context =>
        {
            context.Set<Space>().Add(ProjectEntityFactory.Space(SpaceA, "Alpha"));
            context.Set<ProjectGroup>().Add(ProjectEntityFactory.ProjectGroup(ProjectGroupA, SpaceA, "Alpha Group"));
            context.Set<Project>().AddRange(
                ProjectEntityFactory.Project(1011, SpaceA, ProjectGroupA, "Project A", "project-a"),
                ProjectEntityFactory.Project(1012, SpaceA, ProjectGroupA, "Project B", "project-b"));
            context.Set<Environment>().AddRange(
                ProjectEntityFactory.Environment(2011, SpaceA, "Development", "development"),
                ProjectEntityFactory.Environment(2012, SpaceA, "Production", "production"),
                ProjectEntityFactory.Environment(2013, SpaceA, "Staging", "staging"));

            await context.SaveChangesAsync().ConfigureAwait(false);
        });
    }

    private Task SeedReleaseGraphAsync()
    {
        return Run<SquidDbContext>(async context =>
        {
            context.Set<Space>().AddRange(
                ProjectEntityFactory.Space(SpaceA, "Alpha"),
                ProjectEntityFactory.Space(SpaceB, "Beta"));
            context.Set<ProjectGroup>().AddRange(
                ProjectEntityFactory.ProjectGroup(ProjectGroupA, SpaceA, "Alpha Group"),
                ProjectEntityFactory.ProjectGroup(ProjectGroupB, SpaceB, "Beta Group"));
            context.Set<Project>().AddRange(
                ProjectEntityFactory.Project(1011, SpaceA, ProjectGroupA, "Project A", "project-a"),
                ProjectEntityFactory.Project(1013, SpaceA, ProjectGroupA, "Project A Other", "project-a-other"),
                ProjectEntityFactory.Project(ProjectB, SpaceB, ProjectGroupB, "Project B", "project-b"));

            await context.SaveChangesAsync().ConfigureAwait(false);
        });
    }

    private Task SeedReleasesAsync(params (int Id, int SpaceId, int ProjectId, string Version, DateTimeOffset CreatedAt)[] releases)
    {
        return Run<SquidDbContext>(async context =>
        {
            var entities = releases.Select(item => new Release
            {
                Id = item.Id,
                SpaceId = item.SpaceId,
                ProjectId = item.ProjectId,
                Version = item.Version,
                ChannelId = 1,
                ReleaseNote = string.Empty
            }).ToList();

            context.Set<Release>().AddRange(entities);
            await context.SaveChangesAsync().ConfigureAwait(false);

            foreach (var item in releases)
            {
                var entity = entities.Single(entityItem => entityItem.Id == item.Id);
                entity.CreatedDate = item.CreatedAt;
            }

            await context.SaveChangesAsync().ConfigureAwait(false);
        });
    }

    private Task SeedPagingProjectsAsync()
    {
        return Run<SquidDbContext>(async context =>
        {
            context.Set<Space>().Add(ProjectEntityFactory.Space(SpaceA, "Alpha"));
            context.Set<ProjectGroup>().Add(ProjectEntityFactory.ProjectGroup(ProjectGroupA, SpaceA, "Alpha Group"));
            context.Set<Project>().AddRange(
                ProjectEntityFactory.Project(1011, SpaceA, ProjectGroupA, "Project 1011", "project-1011"),
                ProjectEntityFactory.Project(1012, SpaceA, ProjectGroupA, "Project 1012", "project-1012"),
                ProjectEntityFactory.Project(1013, SpaceA, ProjectGroupA, "Project 1013", "project-1013"),
                ProjectEntityFactory.Project(1014, SpaceA, ProjectGroupA, "Project 1014", "project-1014"));

            await context.SaveChangesAsync().ConfigureAwait(false);
        });
    }

    private static void AssertNotFound<T>(Squid.Message.Response.SquidResponse<T> response)
        where T : class
    {
        response.Code.ShouldBe(HttpStatusCode.NotFound);
        response.Msg.ShouldBe(ResourceNotFoundOrForbidden);
        response.Data.ShouldBeNull();
    }

    private sealed record GrantSeed(
        int TeamSpaceId,
        string RoleName,
        Permission Permission,
        int? RoleSpaceId,
        int[] ProjectIds,
        int[] EnvironmentIds,
        int[] ProjectGroupIds);

    private sealed class FixedCurrentUser(int? id) : ICurrentUser
    {
        public int? Id { get; } = id;
        public string Name => "Scoped Discovery Test User";
        public bool IsInternal => false;
    }

    private sealed class MutableHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext HttpContext { get; set; } = new DefaultHttpContext();

        public void SetHeader(string value)
        {
            HttpContext.Request.Headers.Remove("X-Space-Id");

            if (value != null)
                HttpContext.Request.Headers["X-Space-Id"] = value;
        }
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static class ProjectEntityFactory
    {
        public static Space Space(int id, string name)
        {
            return new Space
            {
                Id = id,
                Name = name,
                Slug = name.ToLowerInvariant(),
                Json = "{}",
                Description = string.Empty
            };
        }

        public static ProjectGroup ProjectGroup(int id, int spaceId, string name)
        {
            return new ProjectGroup
            {
                Id = id,
                SpaceId = spaceId,
                Name = name,
                Slug = name.ToLowerInvariant().Replace(' ', '-'),
                Description = string.Empty
            };
        }

        public static Project Project(int id, int spaceId, int projectGroupId, string name, string slug)
        {
            return new Project
            {
                Id = id,
                SpaceId = spaceId,
                ProjectGroupId = projectGroupId,
                Name = name,
                Slug = slug,
                Json = "{}",
                IncludedLibraryVariableSetIds = "[]",
                IsDisabled = false
            };
        }

        public static Environment Environment(int id, int spaceId, string name, string slug)
        {
            return new Environment
            {
                Id = id,
                SpaceId = spaceId,
                Name = name,
                Slug = slug,
                Description = string.Empty
            };
        }
    }
}

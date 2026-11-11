using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Shouldly;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;
using Xunit;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Variables;

/// <summary>
/// <c>Squid.Deployment.CreatedBy.*</c> (Octopus' <c>Octopus.Deployment.CreatedBy.*</c>) must be defined for
/// EVERY deployment — imported scripts read them, and an undefined name reaches a script as the literal
/// <c>#{…}</c> token. Covers the user row existing, missing (deleted), the internal system user, and no
/// actor at all, plus the precedence against a user-defined variable of the same name.
/// </summary>
public sealed class PrepareDeploymentCreatorVariablesTests
{
    private const int EnvironmentId = 3;

    public static TheoryData<string, int, int, UserAccount[], string, string, string> Creators => new()
    {
        { "DeployedByExisting", 5, 0, [User(5, "alice", "Alice A")], "5", "alice", "Alice A" },
        { "FallsBackToCreatedBy", 0, 5, [User(5, "alice", "Alice A")], "5", "alice", "Alice A" },
        { "DeployedByWinsOverCreatedBy", 5, 9, [User(5, "alice", "Alice A"), User(9, "bob", "Bob")], "5", "alice", "Alice A" },
        { "EmptyDisplayNameFallsBackToUserName", 5, 0, [User(5, "alice", "")], "5", "alice", "alice" },
        { "WhitespaceDisplayNameFallsBackToUserName", 5, 0, [User(5, "alice", "   ")], "5", "alice", "alice" },
        { "NullUserName", 5, 0, [User(5, null, null)], "5", "", "" },
        { "DeletedUser", 77, 0, [User(5, "alice", "Alice A")], "77", "", "" },
        { "InternalSystemUser", CurrentUsers.InternalUser.Id, 0, [User(CurrentUsers.InternalUser.Id, CurrentUsers.InternalUser.Name, CurrentUsers.InternalUser.Name, isSystem: true)], "8888", "internal_user", "internal_user" },
        // A deployment created through an API key carries the key owner's id (ICurrentUser.Id) as DeployedBy.
        { "ApiKeyOwnerServiceAccount", 12, 0, [User(5, "alice", "Alice A"), User(12, "svc-ci", "CI pipeline")], "12", "svc-ci", "CI pipeline" },
        { "DisabledUserStillNamed", 13, 0, [User(13, "bob", "Bob B", isDisabled: true)], "13", "bob", "Bob B" },
        { "NoActor", 0, 0, [User(5, "alice", "Alice A")], "", "", "" },
        { "NegativeActor", -1, 0, [User(5, "alice", "Alice A")], "", "", "" }
    };

    [Theory]
    [MemberData(nameof(Creators))]
    public async Task CreatorVariables_AreAlwaysDefinedExactlyOnce(string caseName, int deployedBy, int createdBy, UserAccount[] users, string expectedId, string expectedUserName, string expectedDisplayName)
    {
        var ctx = await RunPrepareAsync(deployedBy, createdBy, Repository(users));

        Single(ctx, SpecialVariables.Deployment.CreatedById).ShouldBe(expectedId, customMessage: caseName);
        Single(ctx, SpecialVariables.Deployment.CreatedByUsername).ShouldBe(expectedUserName, customMessage: caseName);
        Single(ctx, SpecialVariables.Deployment.CreatedByDisplayName).ShouldBe(expectedDisplayName, customMessage: caseName);
    }

    [Theory]
    [InlineData(42, "42")]
    [InlineData(0, "")]
    public async Task WithoutARepository_CreatorVariablesAreStillDefined(int deployedBy, string expectedId)
    {
        var ctx = await RunPrepareAsync(deployedBy, 0, repository: null);

        Single(ctx, SpecialVariables.Deployment.CreatedById).ShouldBe(expectedId);
        Single(ctx, SpecialVariables.Deployment.CreatedByUsername).ShouldBe(string.Empty);
        Single(ctx, SpecialVariables.Deployment.CreatedByDisplayName).ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(77)]
    [InlineData(0)]
    public async Task ScriptReferencingCreatorVariables_NeverSeesALiteralToken(int deployedBy)
    {
        var ctx = await RunPrepareAsync(deployedBy, 0, Repository(User(5, "alice", "Alice A")));

        var rendered = VariableDictionaryFactory.Create(ctx.Variables).Evaluate("[#{Squid.Deployment.CreatedBy.Id}][#{Squid.Deployment.CreatedBy.Username}][#{Squid.Deployment.CreatedBy.DisplayName}]");

        rendered.ShouldNotContain("#{");
    }

    [Theory]
    [InlineData(5, false, "alice")]
    [InlineData(77, false, "")]
    [InlineData(5, true, "user-defined-for-env")]
    [InlineData(77, true, "user-defined-for-env")]
    public async Task UserDefinedVariableOfTheSameName_PrecedenceIsTheSameWhetherOrNotTheUserExists(int deployedBy, bool environmentScoped, string expected)
    {
        // Pinned: built-in Squid.* names behave like every other built-in (Squid.Project.Name …) — an unscoped
        // user-defined variable of the same name loses, a more specific (environment-scoped) one wins. The
        // outcome no longer depends on whether the triggering user's row still exists.
        var userDefined = new VariableDto { Name = SpecialVariables.Deployment.CreatedByUsername, Value = environmentScoped ? "user-defined-for-env" : "user-defined" };
        if (environmentScoped) userDefined.Scopes = [new VariableScopeDto { ScopeType = VariableScopeType.Environment, ScopeValue = EnvironmentId.ToString() }];

        var ctx = await RunPrepareAsync(deployedBy, 0, Repository(User(5, "alice", "Alice A")), userDefined);
        var effective = VariableScopeEvaluator.Evaluate(ctx.Variables, new VariableScopeContext { EnvironmentId = EnvironmentId });

        effective.Single(v => v.Name == SpecialVariables.Deployment.CreatedByUsername).Value.ShouldBe(expected);
    }

    private static string Single(DeploymentTaskContext ctx, string name)
    {
        var matches = ctx.Variables.Where(v => v.Name == name).ToList();

        matches.Count.ShouldBe(1, customMessage: $"{name} must be defined exactly once");

        return matches[0].Value;
    }

    private static UserAccount User(int id, string userName, string displayName, bool isSystem = false, bool isDisabled = false)
        => new() { Id = id, UserName = userName, DisplayName = displayName, IsSystem = isSystem, IsDisabled = isDisabled };

    private static IRepository Repository(params UserAccount[] users)
    {
        var repository = new Mock<IRepository>();

        repository.Setup(r => r.QueryNoTracking(It.IsAny<Expression<Func<UserAccount, bool>>>()))
            .Returns((Expression<Func<UserAccount, bool>> predicate) => users.AsQueryable().Where(predicate ?? (_ => true)).BuildMock());

        return repository.Object;
    }

    private static async Task<DeploymentTaskContext> RunPrepareAsync(int deployedBy, int createdBy, IRepository repository, params VariableDto[] resolvedVariables)
    {
        var snapshot = new DeploymentProcessSnapshotDto { Id = 1, Data = new DeploymentProcessSnapshotDataDto() };

        var snapshotService = new Mock<IDeploymentSnapshotService>();
        snapshotService.Setup(s => s.LoadProcessSnapshotAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);

        var variableResolver = new Mock<IDeploymentVariableResolver>();
        variableResolver.Setup(r => r.ResolveVariablesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(resolvedVariables.ToList());

        var phase = new PrepareDeploymentPhase(snapshotService.Object, variableResolver.Object, new Mock<IDeploymentTargetFinder>().Object, new Mock<IDeploymentDataProvider>().Object, new Mock<IActionHandlerRegistry>().Object, repository);

        var ctx = new DeploymentTaskContext
        {
            ServerTaskId = 7,
            Task = new ServerTaskEntity { Id = 7 },
            Deployment = new Deployment { Id = 1, EnvironmentId = EnvironmentId, ChannelId = 1, ProcessSnapshotId = 1, DeployedBy = deployedBy, CreatedBy = createdBy },
            Release = new ReleaseEntity { Id = 1, Version = "1.0.0" },
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<ReleaseSelectedPackage>()
        };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        return ctx;
    }
}

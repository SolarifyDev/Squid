using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;
using EnvironmentEntity = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.UnitTests.Services.Deployments.Variables;

/// <summary>
/// D16 seam: the prepare phase asks the imported-secret preflight about the deployment's variables AFTER prompted values are
/// merged, with the deployment's environment and channel, and fails before targets are looked up when anything is missing.
/// </summary>
public sealed class PrepareDeploymentImportedSecretTests
{
    private readonly Mock<IImportedSecretPreflight> _preflight = new();
    private readonly Mock<IDeploymentTargetFinder> _targetFinder = new();
    private IReadOnlyList<VariableDto> _seenVariables;
    private VariableScopeContext _seenScope;
    private int _seenSpaceId;

    public PrepareDeploymentImportedSecretTests()
    {
        Returns([]);
    }

    [Fact]
    public async Task MissingSecret_FailsBeforeTargetsAreLookedUp_NamingIt()
    {
        Returns([new MissingImportedSecret("Db.Password", 601, "", MissingImportedSecretReason.NotImported) { OwnerName = "project \"Web\"" }]);

        var thrown = await Should.ThrowAsync<ImportedSecretMissingException>(() => RunAsync());

        thrown.DeploymentId.ShouldBe(1);
        thrown.Message.ShouldContain("\"Db.Password\" in project \"Web\"");
        _targetFinder.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NothingMissing_Proceeds()
    {
        await RunAsync();

        _preflight.Verify(p => p.FindMissingAsync(It.IsAny<int>(), It.IsAny<IReadOnlyList<VariableDto>>(), It.IsAny<VariableScopeContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Preflight_SeesTheDeploymentsSpaceEnvironmentChannel_AndPromptedAnswers()
    {
        var prompted = new VariableDto { Name = "Db.Password", Value = "", IsSensitive = true, Type = VariableType.Password, VariableSetId = 601, PromptLabel = "Database password" };

        await RunAsync(prompted);

        _seenSpaceId.ShouldBe(5);
        _seenScope.ShouldBe(new VariableScopeContext { EnvironmentId = 3, EnvironmentName = "Production", ChannelId = 4, ChannelName = "Default" });
        _seenVariables.Single(v => v.Name == "Db.Password").Value.ShouldBe("typed-at-deploy", "a prompted answer supplies the secret, so the preflight must see it");
    }

    [Fact]
    public async Task NoPreflightRegistered_Proceeds()
    {
        await RunAsync(preflight: null);
    }

    private void Returns(IReadOnlyList<MissingImportedSecret> missing)
        => _preflight.Setup(p => p.FindMissingAsync(It.IsAny<int>(), It.IsAny<IReadOnlyList<VariableDto>>(), It.IsAny<VariableScopeContext>(), It.IsAny<CancellationToken>()))
            .Callback((int space, IReadOnlyList<VariableDto> variables, VariableScopeContext scope, CancellationToken _) => (_seenSpaceId, _seenVariables, _seenScope) = (space, variables.Select(v => new VariableDto(v)).ToList(), scope))
            .ReturnsAsync(missing);

    private Task RunAsync(params VariableDto[] resolved) => RunAsync(_preflight.Object, resolved);

    private async Task RunAsync(IImportedSecretPreflight preflight, params VariableDto[] resolved)
    {
        var snapshotService = new Mock<IDeploymentSnapshotService>();
        snapshotService.Setup(s => s.LoadProcessSnapshotAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new DeploymentProcessSnapshotDto { Id = 1, Data = new DeploymentProcessSnapshotDataDto() });

        var variableResolver = new Mock<IDeploymentVariableResolver>();
        variableResolver.Setup(r => r.ResolveVariablesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(resolved.ToList());

        _targetFinder.Setup(f => f.FindTargetsAsync(It.IsAny<Deployment>(), It.IsAny<CancellationToken>())).ReturnsAsync([new Machine { Id = 1, Name = "m" }]);

        var phase = new PrepareDeploymentPhase(snapshotService.Object, variableResolver.Object, _targetFinder.Object, new Mock<IDeploymentDataProvider>().Object, new Mock<IActionHandlerRegistry>().Object, null, preflight);

        var ctx = new DeploymentTaskContext
        {
            ServerTaskId = 7,
            Task = new ServerTaskEntity { Id = 7 },
            Deployment = new Deployment { Id = 1, SpaceId = 5, EnvironmentId = 3, ChannelId = 4, ProcessSnapshotId = 1, DeploymentRequestPayload = new DeploymentRequestPayload { FormValues = new Dictionary<string, string> { ["Db.Password"] = "typed-at-deploy" } } },
            Release = new ReleaseEntity { Id = 1, Version = "1.0.0" },
            Environment = new EnvironmentEntity { Id = 3, Name = "Production" },
            Channel = new Channel { Id = 4, Name = "Default" },
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<ReleaseSelectedPackage>()
        };

        await phase.ExecuteAsync(ctx, CancellationToken.None);
    }
}

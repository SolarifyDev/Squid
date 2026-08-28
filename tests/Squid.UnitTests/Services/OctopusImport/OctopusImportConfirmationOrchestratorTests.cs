using System.Linq;
using Mediator.Net;
using Moq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Channels;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.Deployments.Environment;
using Squid.Message.Commands.Deployments.LifeCycle;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Commands.Deployments.Project;
using Squid.Message.Commands.Deployments.ProjectGroup;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Channel;
using Squid.Message.Models.Deployments.Environment;
using Squid.Message.Models.Deployments.LifeCycle;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Project;
using Squid.Message.Models.Deployments.ProjectGroup;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportConfirmationOrchestratorTests
{
    [Fact]
    public async Task ConfirmAsync_SucceedsAndCapturesCreatedAndReusedOutcomes()
    {
        var harness = CreateRichHarness();

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        result.Result.Succeeded.ShouldBeTrue();
        harness.SessionService.CurrentSession.State.ShouldBe(OctopusImportSessionState.Succeeded);
        harness.SessionService.TryStartConfirmationCalls.ShouldBe(1);
        harness.SessionService.RecordResultCalls.ShouldBe(1);
        harness.TransactionExecutor.ExecuteCalls.ShouldBe(1);

        var recorded = harness.SessionService.RecordedResult;
        recorded.ShouldNotBeNull();
        recorded.Succeeded.ShouldBeTrue();
        recorded.Diagnostics.ShouldBeEmpty();

        recorded.Resources.Count.ShouldBe(12);
        recorded.Resources.Single(r => r.SourceId == harness.Nodes.ProjectGroup.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Created);
        recorded.Resources.Single(r => r.SourceId == harness.Nodes.Environment.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Reused);
        recorded.Resources.Single(r => r.SourceId == harness.Nodes.Lifecycle.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Created);
        recorded.Resources.Single(r => r.SourceId == harness.Nodes.LifecyclePhase.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Created);
        recorded.Resources.Single(r => r.SourceId == harness.Nodes.Project.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Created);
        recorded.Resources.Single(r => r.SourceId == harness.Nodes.Channel.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Created);
        recorded.Resources.Single(r => r.SourceId == harness.Nodes.VariableSet.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Created);
        recorded.Resources.Single(r => r.SourceId == harness.Nodes.VariableOne.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Created);
        recorded.Resources.Single(r => r.SourceId == harness.Nodes.VariableTwo.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Created);
        recorded.Resources.Single(r => r.SourceId == harness.Nodes.DeploymentProcess.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Created);
        recorded.Resources.Single(r => r.SourceId == harness.Nodes.DeploymentStep.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Created);
        recorded.Resources.Single(r => r.SourceId == harness.Nodes.DeploymentAction.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Created);

        recorded.IdMappings.Count.ShouldBe(12);
        recorded.IdMappings.Single(m => m.SourceId == harness.Nodes.Environment.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Reused);
        recorded.IdMappings.Single(m => m.SourceId == harness.Nodes.ProjectGroup.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Created);
        recorded.IdMappings.Single(m => m.SourceId == harness.Nodes.Project.SourceId).DestinationId.ShouldBe(1001);
        recorded.IdMappings.Single(m => m.SourceId == harness.Nodes.VariableSet.SourceId).DestinationId.ShouldBe(1002);
        recorded.IdMappings.Single(m => m.SourceId == harness.Nodes.DeploymentProcess.SourceId).DestinationId.ShouldBe(1003);
        harness.ProcessMapper.Invocations
            .Select(invocation => invocation.Arguments[1])
            .OfType<OctopusImportIdMap>()
            .Single()
            .TryGetDestinationId(harness.Nodes.DeploymentProcess.SourceId, OctopusResourceKind.DeploymentProcess.ToString(), out var mappedProcessId)
            .ShouldBeTrue();
        mappedProcessId.ShouldBe(1003);

        harness.Nodes.ChannelUpdate.Captured.ShouldNotBeNull();
        harness.Nodes.ChannelUpdate.Captured.ProjectId.ShouldBe(1001);
        harness.Nodes.ChannelUpdate.Captured.LifecycleId.ShouldBe(13);
    }

    [Fact]
    public async Task ConfirmAsync_UsesExistingMediatorCommandsInDependencyOrder()
    {
        var harness = CreateRichHarness();

        await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        var commandNames = harness.Mediator.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IMediator.SendAsync))
            .Select(invocation => invocation.Arguments[0].GetType().Name)
            .ToList();

        commandNames.ShouldBe(
        [
            nameof(CreateProjectGroupCommand),
            nameof(CreateLifeCycleCommand),
            nameof(CreateProjectCommand),
            nameof(UpdateVariableSetCommand),
            nameof(CreateDeploymentStepCommand)
        ]);
    }

    [Fact]
    public async Task ConfirmAsync_WhenTransactionFails_RollsBackAndPersistsFailedSessionResult()
    {
        var harness = CreateMinimalHarness(throwAfterAction: true, includeReusedEnvironment: true);

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Failed);
        result.Result.Succeeded.ShouldBeFalse();
        harness.SessionService.CurrentSession.State.ShouldBe(OctopusImportSessionState.Failed);
        harness.SessionService.RecordResultCalls.ShouldBe(1);
        harness.TransactionExecutor.ExecuteCalls.ShouldBe(1);

        var recorded = harness.SessionService.RecordedResult;
        recorded.ShouldNotBeNull();
        recorded.Succeeded.ShouldBeFalse();
        recorded.Diagnostics.ShouldContain(d => d.Code == OctopusImportConfirmationDiagnosticCodes.TransactionRolledBack);
        recorded.Resources.Single(r => r.SourceId == harness.Nodes.ProjectGroup.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Failed);
        recorded.Resources.Single(r => r.SourceId == harness.Nodes.Environment.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Reused);
        recorded.Resources.Single(r => r.SourceId == harness.Nodes.Project.SourceId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Failed);
        recorded.Resources.ShouldNotContain(r => r.OutcomeState == OctopusImportResourceOutcomeState.Pending);
        recorded.IdMappings.Count.ShouldBe(1);
        recorded.IdMappings.Single().SourceId.ShouldBe(harness.Nodes.Environment.SourceId);
    }

    [Fact]
    public async Task ConfirmAsync_WhenConfirmationValidationFindsStalePlan_DoesNotExecuteCommands()
    {
        var harness = CreateMinimalHarness();
        harness.PreviewValidator
            .Setup(v => v.Validate(
                It.IsAny<OctopusResourceGraph>(),
                It.IsAny<OctopusImportDependencyPlan>(),
                It.IsAny<OctopusImportConflictDiscoveryResult>(),
                It.IsAny<OctopusImportPreviewPlanDto>()))
            .Returns(new OctopusImportValidationResultDto
            {
                Diagnostics =
                [
                    new OctopusImportDiagnosticDto
                    {
                        Severity = OctopusImportCompatibilitySeverity.Blocker,
                        Code = OctopusImportPreviewDiagnosticCodes.StalePreviewPlan,
                        Message = "The destination resource changed after preview."
                    }
                ]
            });

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Failed);
        result.Result.Succeeded.ShouldBeFalse();
        result.Result.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.StalePreviewPlan);
        result.Result.Diagnostics.ShouldContain(d => d.Code == OctopusImportConfirmationDiagnosticCodes.ValidationBlockedConfirmation);
        harness.TransactionExecutor.ExecuteCalls.ShouldBe(0);
        harness.Mediator.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConfirmAsync_WhenAdmissionIsRejected_ReturnsCurrentImportingSessionWithoutRunningTransaction()
    {
        var harness = CreateMinimalHarness(admissionShouldSucceed: false);

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Importing);
        harness.SessionService.CurrentSession.State.ShouldBe(OctopusImportSessionState.Importing);
        harness.SessionService.TryStartConfirmationCalls.ShouldBe(1);
        harness.SessionService.RecordResultCalls.ShouldBe(0);
        harness.TransactionExecutor.ExecuteCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ConfirmAsync_AllowsOneConcurrentConfirmationAndReturnsImportingToTheOtherCaller()
    {
        var harness = CreateMinimalHarness(withTransactionGate: true);

        var first = harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);
        await harness.TransactionExecutor.Entered.Task.ConfigureAwait(false);

        var second = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None).ConfigureAwait(false);
        second.State.ShouldBe(OctopusImportSessionState.Importing);

        harness.TransactionExecutor.Release.TrySetResult(true);
        var firstResult = await first.ConfigureAwait(false);

        firstResult.State.ShouldBe(OctopusImportSessionState.Succeeded);
        harness.SessionService.CurrentSession.State.ShouldBe(OctopusImportSessionState.Succeeded);
        harness.SessionService.TryStartConfirmationCalls.ShouldBe(1);
        harness.SessionService.RecordResultCalls.ShouldBe(1);
        harness.TransactionExecutor.ExecuteCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ConfirmAsync_IsIdempotentAfterSuccess()
    {
        var harness = CreateMinimalHarness();

        var first = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);
        var second = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        first.State.ShouldBe(OctopusImportSessionState.Succeeded);
        second.State.ShouldBe(OctopusImportSessionState.Succeeded);
        harness.SessionService.CurrentSession.State.ShouldBe(OctopusImportSessionState.Succeeded);
        harness.SessionService.TryStartConfirmationCalls.ShouldBe(1);
        harness.SessionService.RecordResultCalls.ShouldBe(1);
        harness.TransactionExecutor.ExecuteCalls.ShouldBe(1);
    }

    private static TestHarness CreateMinimalHarness(
        bool admissionShouldSucceed = true,
        bool throwAfterAction = false,
        bool withTransactionGate = false,
        bool includeReusedEnvironment = false)
    {
        var nodes = BuildMinimalNodes();
        return CreateHarness(
            nodes,
            BuildDependencyPlan(nodes.MinimalOrderedResources),
            BuildPreviewPlan(
                nodes.MinimalOrderedResources,
                reusedResources: includeReusedEnvironment ? [nodes.Environment] : null),
            admissionShouldSucceed,
            throwAfterAction,
            withTransactionGate);
    }

    private static TestHarness CreateRichHarness()
    {
        var nodes = BuildRichNodes();
        return CreateHarness(
            nodes,
            BuildDependencyPlan(nodes.RichOrderedResources),
            BuildPreviewPlan(nodes.RichOrderedResources, reusedResources: [nodes.Environment]),
            admissionShouldSucceed: true,
            throwAfterAction: false,
            withTransactionGate: false);
    }

    private static TestHarness CreateHarness(
        ScenarioNodes nodes,
        OctopusImportDependencyPlan dependencyPlan,
        OctopusImportPreviewPlanDto previewPlan,
        bool admissionShouldSucceed,
        bool throwAfterAction,
        bool withTransactionGate)
    {
        var sessionService = new TestOctopusImportSessionService(
            nodes.SessionId,
            nodes.DestinationSpaceId,
            previewPlan)
        {
            AdmissionShouldSucceed = admissionShouldSucceed
        };

        var transactionExecutor = new TestOctopusImportTransactionExecutor
        {
            ThrowAfterAction = throwAfterAction,
            UseGate = withTransactionGate
        };

        var projectMapper = new Mock<IOctopusImportProjectMapper>();
        var environmentMapper = new Mock<IOctopusImportEnvironmentMapper>();
        var lifecycleMapper = new Mock<IOctopusImportLifecycleMapper>();
        var feedMapper = new Mock<IOctopusImportFeedMapper>();
        var variableMapper = new Mock<IOctopusImportVariableMapper>();
        var processMapper = new Mock<IOctopusImportDeploymentProcessMapper>();
        var shellMapper = new Mock<IOctopusImportExternalResourceShellMapper>();
        var mediator = new Mock<IMediator>();
        var projectDataProvider = new Mock<IProjectDataProvider>();
        var channelDataProvider = new Mock<IChannelDataProvider>();
        var conflictDiscoveryService = new Mock<IOctopusImportConflictDiscoveryService>();
        var previewValidator = new Mock<IOctopusImportPreviewValidator>();

        conflictDiscoveryService
            .Setup(s => s.DiscoverAsync(nodes.DestinationSpaceId, It.IsAny<OctopusResourceGraph>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctopusImportConflictDiscoveryResult([]));

        previewValidator
            .Setup(v => v.Validate(It.IsAny<OctopusResourceGraph>(), It.IsAny<OctopusImportDependencyPlan>(), It.IsAny<OctopusImportConflictDiscoveryResult>(), It.IsAny<OctopusImportPreviewPlanDto>()))
            .Returns(new OctopusImportValidationResultDto());

        SetupMappers(
            nodes,
            projectMapper,
            environmentMapper,
            lifecycleMapper,
            feedMapper,
            variableMapper,
            processMapper,
            shellMapper,
            nodes.DestinationSpaceId);

        SetupMediatorResponses(
            mediator,
            nodes,
            projectDataProvider,
            channelDataProvider);

        var orchestrator = new OctopusImportConfirmationOrchestrator(
            sessionService,
            conflictDiscoveryService.Object,
            previewValidator.Object,
            transactionExecutor,
            projectMapper.Object,
            environmentMapper.Object,
            lifecycleMapper.Object,
            feedMapper.Object,
            variableMapper.Object,
            processMapper.Object,
            shellMapper.Object,
            projectDataProvider.Object,
            channelDataProvider.Object,
            mediator.Object);

        return new TestHarness(
            orchestrator,
            sessionService,
            transactionExecutor,
            projectMapper,
            environmentMapper,
            lifecycleMapper,
            feedMapper,
            variableMapper,
            processMapper,
            shellMapper,
            mediator,
            projectDataProvider,
            channelDataProvider,
            previewValidator,
            nodes,
            dependencyPlan,
            previewPlan,
            new OctopusImportConfirmationRequest(
                nodes.SessionId,
                nodes.DestinationSpaceId,
                new OctopusResourceGraph(dependencyPlan.OrderedResources, [], [], []),
                dependencyPlan,
                previewPlan));
    }

    private static void SetupMappers(
        ScenarioNodes nodes,
        Mock<IOctopusImportProjectMapper> projectMapper,
        Mock<IOctopusImportEnvironmentMapper> environmentMapper,
        Mock<IOctopusImportLifecycleMapper> lifecycleMapper,
        Mock<IOctopusImportFeedMapper> feedMapper,
        Mock<IOctopusImportVariableMapper> variableMapper,
        Mock<IOctopusImportDeploymentProcessMapper> processMapper,
        Mock<IOctopusImportExternalResourceShellMapper> shellMapper,
        int destinationSpaceId)
    {
        projectMapper
            .Setup(m => m.MapToCreateOrUpdateModel(
                It.IsAny<OctopusResourceNode>(),
                It.IsAny<OctopusImportIdMap>(),
                destinationSpaceId,
                It.IsAny<OctopusDeploymentSettingsDto>(),
                It.IsAny<OctopusChannelDto>()))
            .Returns((OctopusResourceNode projectResource, OctopusImportIdMap idMap, int spaceId, OctopusDeploymentSettingsDto deploymentSettings, OctopusChannelDto defaultChannel) =>
            {
                var project = projectResource.GetSource<OctopusProjectDto>();
                return new OctopusImportProjectMappingResult(
                    new CreateOrUpdateProjectModel
                    {
                        Name = project.Name,
                        Slug = project.Slug,
                        SpaceId = spaceId,
                        ProjectGroupId = 11,
                        LifecycleId = 13,
                        IncludedLibraryVariableSetIds = []
                    },
                    []);
            });

        environmentMapper
            .Setup(m => m.MapToCreateModel(It.IsAny<OctopusResourceNode>(), destinationSpaceId))
            .Returns((OctopusResourceNode resource, int spaceId) =>
            {
                var source = resource.GetSource<OctopusEnvironmentDto>();
                return new OctopusImportEnvironmentMappingResult(
                    new CreateEnvironmentCommand
                    {
                        SpaceId = spaceId,
                        Name = source.Name,
                        Slug = source.Slug,
                        Description = source.Description,
                        SortOrder = source.SortOrder,
                        UseGuidedFailure = source.UseGuidedFailure ?? false,
                        AllowDynamicInfrastructure = source.AllowDynamicInfrastructure ?? false
                    },
                    []);
            });

        lifecycleMapper
            .Setup(m => m.MapToCreateOrUpdateModel(It.IsAny<OctopusResourceNode>(), It.IsAny<OctopusImportIdMap>(), destinationSpaceId))
            .Returns((OctopusResourceNode resource, OctopusImportIdMap idMap, int spaceId) =>
            {
                var source = resource.GetSource<OctopusLifecycleDto>();
                return new OctopusImportLifecycleMappingResult(
                    new CreateOrUpdateLifeCycleModel
                    {
                        Lifecycle = new LifeCycleModel
                        {
                            Name = source.Name,
                            Slug = source.Slug,
                            SpaceId = spaceId
                        },
                        Phases = [new LifecyclePhaseModel { Name = "Phase 1", SortOrder = 1 }]
                    },
                    []);
            });

        feedMapper
            .Setup(m => m.MapToCreateCommand(It.IsAny<OctopusResourceNode>(), destinationSpaceId))
            .Returns(new OctopusImportFeedMappingResult(null, null, [], []));

        variableMapper
            .Setup(m => m.MapToUpdateCommand(
                It.IsAny<OctopusResourceNode>(),
                It.IsAny<OctopusImportIdMap>(),
                It.IsAny<int>(),
                destinationSpaceId,
                null,
                null))
            .Returns((OctopusResourceNode resource, OctopusImportIdMap idMap, int destinationVariableSetId, int spaceId, string name, string description) =>
            {
                var source = resource.GetSource<OctopusVariableSetDto>();
                return new OctopusImportVariableMappingResult(
                    null,
                    new UpdateVariableSetCommand
                    {
                        Id = destinationVariableSetId,
                        OwnerId = 1001,
                        OwnerType = VariableSetOwnerType.Project,
                        SpaceId = spaceId,
                        Variables = source.Variables.Select(variable => new VariableModel
                        {
                            Name = variable.Name,
                            Value = variable.Value,
                            Type = Enum.TryParse<VariableType>(variable.Type, true, out var parsedType)
                                ? parsedType
                                : VariableType.String
                        }).ToList()
                    },
                    [],
                    []);
            });

        processMapper
            .Setup(m => m.MapToCreateStepCommands(It.IsAny<OctopusResourceNode>(), It.IsAny<OctopusImportIdMap>(), destinationSpaceId))
            .Returns((OctopusResourceNode resource, OctopusImportIdMap idMap, int spaceId) =>
            {
                var source = resource.GetSource<OctopusDeploymentProcessDto>();
                var step = source.Steps.Single();
                var action = step.Actions.Single();
                idMap.TryGetDestinationId(source.Id, OctopusResourceKind.DeploymentProcess.ToString(), out var processId).ShouldBeTrue();

                return new OctopusImportDeploymentProcessMappingResult(
                    [
                        new OctopusImportDeploymentStepCommandMapping(
                            step.Id,
                            step.Name,
                            0,
                            new CreateDeploymentStepCommand
                            {
                                ProcessId = processId,
                                SpaceId = spaceId,
                                Step = new CreateOrUpdateDeploymentStepModel
                                {
                                    Name = step.Name,
                                    StepType = "Action",
                                    Actions =
                                    [
                                        new CreateOrUpdateDeploymentActionModel
                                        {
                                            Name = action.Name,
                                            ActionType = "Squid.Script"
                                        }
                                    ]
                                }
                            },
                            [
                                new OctopusImportDeploymentActionModelMapping(
                                    action.Id,
                                    action.Name,
                                    0,
                                    new CreateOrUpdateDeploymentActionModel
                                    {
                                        Name = action.Name,
                                        ActionType = "Squid.Script"
                                    })
                            ])
                    ],
                    []);
            });

        shellMapper
            .Setup(m => m.MapAccountToCreateCommand(It.IsAny<OctopusResourceNode>(), It.IsAny<OctopusImportIdMap>(), destinationSpaceId))
            .Returns(new OctopusImportAccountShellMappingResult(null, [], []));
        shellMapper
            .Setup(m => m.MapCertificateToManualShell(It.IsAny<OctopusResourceNode>()))
            .Returns(new OctopusImportCertificateShellMappingResult(new OctopusImportCertificateShell(string.Empty, null, false, null, null, false), [], []));
        shellMapper
            .Setup(m => m.MapTargetToManualShell(It.IsAny<OctopusResourceNode>(), It.IsAny<OctopusImportIdMap>(), destinationSpaceId))
            .Returns(new OctopusImportTargetShellMappingResult(new OctopusImportTargetShell(string.Empty, false, Array.Empty<string>(), Array.Empty<int>(), null, new Dictionary<string, string>(), true), [], []));
    }

    private static void SetupMediatorResponses(
        Mock<IMediator> mediator,
        ScenarioNodes nodes,
        Mock<IProjectDataProvider> projectDataProvider,
        Mock<IChannelDataProvider> channelDataProvider)
    {
        mediator
            .Setup(m => m.SendAsync<CreateProjectGroupCommand, CreateProjectGroupResponse>(It.IsAny<CreateProjectGroupCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateProjectGroupResponse
            {
                Data = new ProjectGroupDto
                {
                    Id = 11,
                    Name = nodes.ProjectGroup.Name,
                    Slug = nodes.ProjectGroup.GetSource<OctopusProjectGroupDto>().Slug,
                    SpaceId = nodes.DestinationSpaceId
                }
            });

        mediator
            .Setup(m => m.SendAsync<CreateEnvironmentCommand, CreateEnvironmentResponse>(It.IsAny<CreateEnvironmentCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateEnvironmentResponse
            {
                Data = new CreateEnvironmentResponseData
                {
                    Environment = new EnvironmentDto
                    {
                        Id = 12,
                        Name = nodes.Environment.Name,
                        Slug = nodes.Environment.GetSource<OctopusEnvironmentDto>().Slug,
                        SpaceId = nodes.DestinationSpaceId
                    }
                }
            });

        mediator
            .Setup(m => m.SendAsync<CreateProjectCommand, CreateProjectResponse>(It.IsAny<CreateProjectCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateProjectResponse
            {
                Data = new ProjectDto
                {
                    Id = 1001,
                    Name = nodes.Project.Name,
                    Slug = nodes.Project.GetSource<OctopusProjectDto>().Slug,
                    SpaceId = nodes.DestinationSpaceId,
                    VariableSetId = 1002,
                    DeploymentProcessId = 1003
                }
            });

        if (nodes.Lifecycle != null)
        {
            mediator
                .Setup(m => m.SendAsync<CreateLifeCycleCommand, CreateLifeCycleResponse>(It.IsAny<CreateLifeCycleCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CreateLifeCycleResponse
                {
                    Data = new CreateLifeCycleResponseData
                    {
                        LifecyclePhase = new LifecycleDetailDto
                        {
                            Lifecycle = new LifeCycleDto
                            {
                                Id = 13,
                                Name = nodes.Lifecycle.Name,
                                Slug = nodes.Lifecycle.GetSource<OctopusLifecycleDto>().Slug,
                                SpaceId = nodes.DestinationSpaceId
                            },
                            Phases =
                            [
                                new LifecyclePhaseDto
                                {
                                    Id = 14,
                                    LifecycleId = 13,
                                    Name = nodes.LifecyclePhase.Name,
                                    SortOrder = 1
                                }
                            ]
                        }
                    }
                });
        }

        if (nodes.VariableSet != null)
        {
            mediator
                .Setup(m => m.SendAsync<UpdateVariableSetCommand, UpdateVariableSetResponse>(It.IsAny<UpdateVariableSetCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateVariableSetResponse
                {
                    Data = new UpdateVariableSetResponseData
                    {
                        VariableSet = new VariableSetDto
                        {
                            Id = 1002,
                            Name = nodes.VariableSet.Name,
                            OwnerId = 1001,
                            OwnerType = VariableSetOwnerType.Project,
                            SpaceId = nodes.DestinationSpaceId,
                            Variables =
                            [
                                new VariableDto { Id = 1004, Name = nodes.VariableOne.Name, Value = nodes.VariableOne.GetSource<OctopusVariableDto>().Value },
                                new VariableDto { Id = 1005, Name = nodes.VariableTwo.Name, Value = nodes.VariableTwo.GetSource<OctopusVariableDto>().Value }
                            ]
                        }
                    }
                });
        }

        if (nodes.DeploymentProcess != null)
        {
            mediator
                .Setup(m => m.SendAsync<CreateDeploymentStepCommand, CreateDeploymentStepResponse>(It.IsAny<CreateDeploymentStepCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CreateDeploymentStepResponse
                {
                    Data = new DeploymentStepDto
                    {
                        Id = 1006,
                        ProcessId = 1003,
                        Name = nodes.DeploymentStep.Name,
                        Actions =
                        [
                            new DeploymentActionDto
                            {
                                Id = 1007,
                                StepId = 1006,
                                Name = nodes.DeploymentAction.Name,
                                ActionType = "Squid.Script"
                            }
                        ]
                    }
                });
        }

        if (nodes.Channel != null)
        {
            channelDataProvider
                .Setup(p => p.GetDefaultChannelByProjectIdAsync(1001, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Channel
                {
                    Id = 1008,
                    Name = "Default",
                    Description = "Default",
                    ProjectId = 1001,
                    LifecycleId = null,
                    SpaceId = nodes.DestinationSpaceId,
                    Slug = "default",
                    IsDefault = true
                });

            channelDataProvider
                .Setup(p => p.UpdateChannelAsync(It.IsAny<Channel>(), false, It.IsAny<CancellationToken>()))
                .Callback<Channel, bool, CancellationToken>((channel, forceSave, cancellationToken) => nodes.ChannelUpdate.Captured = CloneChannel(channel))
                .Returns(Task.CompletedTask);
        }

        projectDataProvider
            .Setup(p => p.GetProjectByIdAsync(1001, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project
            {
                Id = 1001,
                Name = nodes.Project.Name,
                Slug = nodes.Project.GetSource<OctopusProjectDto>().Slug,
                SpaceId = nodes.DestinationSpaceId,
                VariableSetId = 1002,
                DeploymentProcessId = 1003
            });
    }

    private static ScenarioNodes BuildMinimalNodes()
    {
        var destinationSpaceId = 7;
        var sessionId = Guid.NewGuid();

        var projectGroup = Node(
            "ProjectsGroups-1",
            OctopusResourceKind.ProjectGroup,
            OctopusDocumentKind.ProjectGroup,
            "Project Group",
            new OctopusProjectGroupDto
            {
                Id = "ProjectsGroups-1",
                Name = "Project Group",
                Slug = "project-group"
            },
            null);

        var environment = Node(
            "Environments-1",
            OctopusResourceKind.Environment,
            OctopusDocumentKind.Environment,
            "Production",
            new OctopusEnvironmentDto
            {
                Id = "Environments-1",
                Name = "Production",
                Slug = "production"
            },
            null);

        var project = Node(
            "Projects-1",
            OctopusResourceKind.Project,
            OctopusDocumentKind.Project,
            "App",
            new OctopusProjectDto
            {
                Id = "Projects-1",
                Name = "App",
                Slug = "app",
                ProjectGroupId = "ProjectsGroups-1",
                LifecycleId = "Lifecycles-1"
            },
            null);

        var ordered = new List<OctopusResourceNode> { projectGroup, environment, project };

        return new ScenarioNodes(
            sessionId,
            destinationSpaceId,
            projectGroup,
            environment,
            null,
            null,
            project,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            ordered,
            ordered,
            null);
    }

    private static ScenarioNodes BuildRichNodes()
    {
        var destinationSpaceId = 7;
        var sessionId = Guid.NewGuid();

        var projectGroup = Node(
            "ProjectsGroups-1",
            OctopusResourceKind.ProjectGroup,
            OctopusDocumentKind.ProjectGroup,
            "Project Group",
            new OctopusProjectGroupDto
            {
                Id = "ProjectsGroups-1",
                Name = "Project Group",
                Slug = "project-group"
            },
            null);

        var environment = Node(
            "Environments-1",
            OctopusResourceKind.Environment,
            OctopusDocumentKind.Environment,
            "Production",
            new OctopusEnvironmentDto
            {
                Id = "Environments-1",
                Name = "Production",
                Slug = "production"
            },
            null);

        var lifecyclePhase = Node(
            "LifecyclePhases-1",
            OctopusResourceKind.LifecyclePhase,
            OctopusDocumentKind.Lifecycle,
            "Phase 1",
            new OctopusLifecyclePhaseDto
            {
                Id = "LifecyclePhases-1",
                Name = "Phase 1",
                MinimumEnvironmentsBeforePromotion = 1
            },
            "Lifecycles-1");

        var lifecycle = Node(
            "Lifecycles-1",
            OctopusResourceKind.Lifecycle,
            OctopusDocumentKind.Lifecycle,
            "Standard",
            new OctopusLifecycleDto
            {
                Id = "Lifecycles-1",
                Name = "Standard",
                Slug = "standard",
                Phases =
                [
                    new OctopusLifecyclePhaseDto
                    {
                        Id = "LifecyclePhases-1",
                        Name = "Phase 1",
                        MinimumEnvironmentsBeforePromotion = 1
                    }
                ]
            },
            null);

        var channel = Node(
            "Channels-1",
            OctopusResourceKind.Channel,
            OctopusDocumentKind.Channel,
            "Default",
            new OctopusChannelDto
            {
                Id = "Channels-1",
                Name = "Default",
                Slug = "default",
                ProjectId = "Projects-1",
                LifecycleId = "Lifecycles-1",
                IsDefault = true
            },
            "Projects-1");

        var project = Node(
            "Projects-1",
            OctopusResourceKind.Project,
            OctopusDocumentKind.Project,
            "App",
            new OctopusProjectDto
            {
                Id = "Projects-1",
                Name = "App",
                Slug = "app",
                ProjectGroupId = "ProjectsGroups-1",
                LifecycleId = "Lifecycles-1"
            },
            null);

        var variableOne = Node(
            "Variables-1",
            OctopusResourceKind.Variable,
            OctopusDocumentKind.VariableSet,
            "ApiKey",
            new OctopusVariableDto
            {
                Id = "Variables-1",
                Name = "ApiKey",
                Type = "String",
                Value = "secret-1"
            },
            "VariableSets-1");

        var variableTwo = Node(
            "Variables-2",
            OctopusResourceKind.Variable,
            OctopusDocumentKind.VariableSet,
            "Region",
            new OctopusVariableDto
            {
                Id = "Variables-2",
                Name = "Region",
                Type = "String",
                Value = "ap-southeast-1"
            },
            "VariableSets-1");

        var variableSet = Node(
            "VariableSets-1",
            OctopusResourceKind.VariableSet,
            OctopusDocumentKind.VariableSet,
            "Variables",
            new OctopusVariableSetDto
            {
                Id = "VariableSets-1",
                OwnerId = "Projects-1",
                OwnerType = "Project",
                Variables =
                [
                    new OctopusVariableDto
                    {
                        Id = "Variables-1",
                        Name = "ApiKey",
                        Type = "String",
                        Value = "secret-1"
                    },
                    new OctopusVariableDto
                    {
                        Id = "Variables-2",
                        Name = "Region",
                        Type = "String",
                        Value = "ap-southeast-1"
                    }
                ]
            },
            "Projects-1");

        var deploymentAction = Node(
            "Actions-1",
            OctopusResourceKind.DeploymentAction,
            OctopusDocumentKind.DeploymentProcess,
            "Run",
            new OctopusDeploymentActionDto
            {
                Id = "Actions-1",
                Name = "Run",
                ActionType = "Octopus.Script"
            },
            "Steps-1");

        var deploymentStep = Node(
            "Steps-1",
            OctopusResourceKind.DeploymentStep,
            OctopusDocumentKind.DeploymentProcess,
            "Deploy",
            new OctopusDeploymentStepDto
            {
                Id = "Steps-1",
                Name = "Deploy",
                Actions =
                [
                    new OctopusDeploymentActionDto
                    {
                        Id = "Actions-1",
                        Name = "Run",
                        ActionType = "Octopus.Script"
                    }
                ]
            },
            "DeploymentProcesses-1");

        var deploymentProcess = Node(
            "DeploymentProcesses-1",
            OctopusResourceKind.DeploymentProcess,
            OctopusDocumentKind.DeploymentProcess,
            "Process",
            new OctopusDeploymentProcessDto
            {
                Id = "DeploymentProcesses-1",
                OwnerId = "Projects-1",
                Steps =
                [
                    new OctopusDeploymentStepDto
                    {
                        Id = "Steps-1",
                        Name = "Deploy",
                        Actions =
                        [
                            new OctopusDeploymentActionDto
                            {
                                Id = "Actions-1",
                                Name = "Run",
                                ActionType = "Octopus.Script"
                            }
                        ]
                    }
                ]
            },
            "Projects-1");

        var ordered = new List<OctopusResourceNode>
        {
            projectGroup,
            environment,
            lifecycle,
            lifecyclePhase,
            project,
            channel,
            variableSet,
            variableOne,
            variableTwo,
            deploymentProcess,
            deploymentStep,
            deploymentAction
        };

        return new ScenarioNodes(
            sessionId,
            destinationSpaceId,
            projectGroup,
            environment,
            lifecycle,
            lifecyclePhase,
            project,
            channel,
            variableSet,
            variableOne,
            variableTwo,
            deploymentProcess,
            deploymentStep,
            deploymentAction,
            ordered,
            ordered,
            new ChannelUpdateCapture());
    }

    private static OctopusImportDependencyPlan BuildDependencyPlan(IReadOnlyList<OctopusResourceNode> orderedResources)
        => new(orderedResources, [], [], []);

    private static OctopusImportPreviewPlanDto BuildPreviewPlan(
        IReadOnlyList<OctopusResourceNode> orderedResources,
        IReadOnlyList<OctopusResourceNode> reusedResources = null)
    {
        reusedResources ??= [];

        return new OctopusImportPreviewPlanDto
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources = orderedResources.Select(resource =>
            {
                var isReused = reusedResources.Any(r => string.Equals(r.SourceId, resource.SourceId, StringComparison.OrdinalIgnoreCase));
                return new OctopusImportResourceResultDto
                {
                    SourceId = resource.SourceId,
                    SourceType = resource.Kind.ToString(),
                    SourceName = resource.Name,
                    PreviewAction = isReused ? OctopusImportPreviewAction.ReuseExisting : OctopusImportPreviewAction.Create,
                    OutcomeState = isReused ? OctopusImportResourceOutcomeState.Reused : OctopusImportResourceOutcomeState.Pending,
                    DestinationId = isReused ? 200 : null
                };
            }).ToList()
        };
    }

    private static OctopusImportSessionResultDto BuildInitialResult(OctopusImportPreviewPlanDto previewPlan)
        => new()
        {
            Succeeded = false,
            Resources = previewPlan.Resources.Select(resource => new OctopusImportResourceResultDto
            {
                SourceId = resource.SourceId,
                SourceType = resource.SourceType,
                SourceName = resource.SourceName,
                PreviewAction = resource.PreviewAction,
                OutcomeState = resource.OutcomeState,
                DestinationId = resource.DestinationId,
                RequiredInputs = resource.RequiredInputs.Select(input => new OctopusImportRequiredInputDto
                {
                    InputKey = input.InputKey,
                    Kind = input.Kind,
                    SourceId = input.SourceId,
                    SourceType = input.SourceType,
                    Name = input.Name,
                    FieldName = input.FieldName,
                    ValueType = input.ValueType,
                    HasSourceValue = input.HasSourceValue,
                    IsRequired = input.IsRequired,
                    SourceScopes = input.SourceScopes.ToDictionary(pair => pair.Key, pair => pair.Value.ToList())
                }).ToList(),
                Diagnostics = resource.Diagnostics.Select(diagnostic => new OctopusImportDiagnosticDto
                {
                    Severity = diagnostic.Severity,
                    Code = diagnostic.Code,
                    Message = diagnostic.Message,
                    ResourceType = diagnostic.ResourceType,
                    SourceId = diagnostic.SourceId,
                    ResourceName = diagnostic.ResourceName
                }).ToList()
            }).ToList(),
            Diagnostics = previewPlan.Diagnostics.Select(diagnostic => new OctopusImportDiagnosticDto
            {
                Severity = diagnostic.Severity,
                Code = diagnostic.Code,
                Message = diagnostic.Message,
                ResourceType = diagnostic.ResourceType,
                SourceId = diagnostic.SourceId,
                ResourceName = diagnostic.ResourceName
            }).ToList()
        };

    private static OctopusResourceNode Node(
        string sourceId,
        OctopusResourceKind kind,
        OctopusDocumentKind documentKind,
        string name,
        object source,
        string parentSourceId)
        => new(
            sourceId,
            name,
            kind,
            documentKind,
            $"{sourceId}.json",
            "Projects-1",
            parentSourceId,
            false,
            source);

    private static Channel CloneChannel(Channel channel)
        => new()
        {
            Id = channel.Id,
            Name = channel.Name,
            Description = channel.Description,
            ProjectId = channel.ProjectId,
            LifecycleId = channel.LifecycleId,
            SpaceId = channel.SpaceId,
            Slug = channel.Slug,
            IsDefault = channel.IsDefault,
            CreatedDate = channel.CreatedDate,
            CreatedBy = channel.CreatedBy,
            LastModifiedDate = channel.LastModifiedDate,
            LastModifiedBy = channel.LastModifiedBy
        };

    private sealed record TestHarness(
        OctopusImportConfirmationOrchestrator Sut,
        TestOctopusImportSessionService SessionService,
        TestOctopusImportTransactionExecutor TransactionExecutor,
        Mock<IOctopusImportProjectMapper> ProjectMapper,
        Mock<IOctopusImportEnvironmentMapper> EnvironmentMapper,
        Mock<IOctopusImportLifecycleMapper> LifecycleMapper,
        Mock<IOctopusImportFeedMapper> FeedMapper,
        Mock<IOctopusImportVariableMapper> VariableMapper,
        Mock<IOctopusImportDeploymentProcessMapper> ProcessMapper,
        Mock<IOctopusImportExternalResourceShellMapper> ShellMapper,
        Mock<IMediator> Mediator,
        Mock<IProjectDataProvider> ProjectDataProvider,
        Mock<IChannelDataProvider> ChannelDataProvider,
        Mock<IOctopusImportPreviewValidator> PreviewValidator,
        ScenarioNodes Nodes,
        OctopusImportDependencyPlan DependencyPlan,
        OctopusImportPreviewPlanDto PreviewPlan,
        OctopusImportConfirmationRequest Request);

    private sealed record ChannelUpdateCapture
    {
        public Channel Captured { get; set; }
    }

    private sealed record ScenarioNodes(
        Guid SessionId,
        int DestinationSpaceId,
        OctopusResourceNode ProjectGroup,
        OctopusResourceNode Environment,
        OctopusResourceNode Lifecycle,
        OctopusResourceNode LifecyclePhase,
        OctopusResourceNode Project,
        OctopusResourceNode Channel,
        OctopusResourceNode VariableSet,
        OctopusResourceNode VariableOne,
        OctopusResourceNode VariableTwo,
        OctopusResourceNode DeploymentProcess,
        OctopusResourceNode DeploymentStep,
        OctopusResourceNode DeploymentAction,
        IReadOnlyList<OctopusResourceNode> MinimalOrderedResources,
        IReadOnlyList<OctopusResourceNode> RichOrderedResources,
        ChannelUpdateCapture ChannelUpdate);

    private sealed class TestOctopusImportSessionService : IOctopusImportSessionService
    {
        public TestOctopusImportSessionService(
            Guid sessionId,
            int destinationSpaceId,
            OctopusImportPreviewPlanDto previewPlan)
        {
            CurrentSession = new OctopusImportSessionDto
            {
                SessionId = sessionId,
                DestinationSpaceId = destinationSpaceId,
                OwnerUserId = 42,
                State = OctopusImportSessionState.Validated,
                Result = BuildInitialResult(previewPlan),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                LastStateChangedAt = DateTimeOffset.UtcNow
            };
        }

        public OctopusImportSessionDto CurrentSession { get; private set; }

        public OctopusImportSessionResultDto RecordedResult { get; private set; }

        public int GetSessionCalls { get; private set; }

        public int TryStartConfirmationCalls { get; private set; }

        public int RecordResultCalls { get; private set; }

        public bool AdmissionShouldSucceed { get; set; } = true;

        public Task<OctopusImportSessionDto> CreateSessionAsync(int destinationSpaceId, OctopusImportSourceSummaryDto sourceSummary, DateTimeOffset expiresAt, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OctopusImportSessionDto> CreateSessionAsync(int destinationSpaceId, OctopusImportSourceSummaryDto sourceSummary, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OctopusImportSessionDto> RegisterTemporaryUploadAsync(Guid sessionId, int destinationSpaceId, OctopusImportTemporaryUpload temporaryUpload, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OctopusImportSessionDto> RegisterTemporaryUploadAsync(Guid sessionId, int destinationSpaceId, OctopusImportTemporaryUpload temporaryUpload, OctopusImportSourceSummaryDto sourceSummary, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OctopusImportSessionDto> UpdatePayloadAndTransitionAsync(Guid sessionId, int destinationSpaceId, OctopusImportSessionState expectedState, OctopusImportSessionState newState, string redactedNormalizedDataJson = null, string validatedPlanJson = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> ExpireSessionsAsync(DateTimeOffset now, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OctopusImportSessionDto> GetSessionAsync(Guid sessionId, int destinationSpaceId, CancellationToken ct = default)
        {
            GetSessionCalls++;
            return Task.FromResult(CloneSession(CurrentSession));
        }

        public Task<bool> TryStartConfirmationAsync(Guid sessionId, int destinationSpaceId, CancellationToken ct = default)
        {
            TryStartConfirmationCalls++;

            if (CurrentSession.State != OctopusImportSessionState.Validated)
                return Task.FromResult(false);

            if (AdmissionShouldSucceed)
            {
                CurrentSession.State = OctopusImportSessionState.Importing;
                CurrentSession.LastStateChangedAt = DateTimeOffset.UtcNow;
                return Task.FromResult(true);
            }

            CurrentSession.State = OctopusImportSessionState.Importing;
            CurrentSession.LastStateChangedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(false);
        }

        public Task<OctopusImportSessionDto> RecordResultAsync(Guid sessionId, int destinationSpaceId, OctopusImportSessionState terminalState, OctopusImportSessionResultDto result, CancellationToken ct = default)
        {
            RecordResultCalls++;

            RecordedResult = CloneResult(result);
            RecordedResult.Succeeded = terminalState == OctopusImportSessionState.Succeeded;
            RecordedResult.CompletedAt = DateTimeOffset.UtcNow;

            CurrentSession.State = terminalState;
            CurrentSession.Result = CloneResult(RecordedResult);
            CurrentSession.CompletedAt = RecordedResult.CompletedAt;
            CurrentSession.LastStateChangedAt = RecordedResult.CompletedAt;

            return Task.FromResult(CloneSession(CurrentSession));
        }

        private static OctopusImportSessionDto CloneSession(OctopusImportSessionDto session)
            => session == null
                ? null
                : new OctopusImportSessionDto
                {
                    SessionId = session.SessionId,
                    DestinationSpaceId = session.DestinationSpaceId,
                    OwnerUserId = session.OwnerUserId,
                    State = session.State,
                    SourceSummary = session.SourceSummary,
                    Result = CloneResult(session.Result),
                    ExpiresAt = session.ExpiresAt,
                    CompletedAt = session.CompletedAt,
                    LastStateChangedAt = session.LastStateChangedAt
                };

        private static OctopusImportSessionResultDto CloneResult(OctopusImportSessionResultDto result)
            => result == null
                ? null
                : new OctopusImportSessionResultDto
                {
                    Succeeded = result.Succeeded,
                    CompletedAt = result.CompletedAt,
                    Resources = result.Resources.Select(CloneResource).ToList(),
                    IdMappings = result.IdMappings.Select(CloneMapping).ToList(),
                    Diagnostics = result.Diagnostics.Select(CloneDiagnostic).ToList()
                };

        private static OctopusImportResourceResultDto CloneResource(OctopusImportResourceResultDto resource)
            => resource == null
                ? null
                : new OctopusImportResourceResultDto
                {
                    SourceId = resource.SourceId,
                    SourceType = resource.SourceType,
                    SourceName = resource.SourceName,
                    PreviewAction = resource.PreviewAction,
                    OutcomeState = resource.OutcomeState,
                    DestinationId = resource.DestinationId,
                    RequiredInputs = resource.RequiredInputs.Select(CloneRequiredInput).ToList(),
                    Diagnostics = resource.Diagnostics.Select(CloneDiagnostic).ToList()
                };

        private static OctopusImportRequiredInputDto CloneRequiredInput(OctopusImportRequiredInputDto input)
            => input == null
                ? null
                : new OctopusImportRequiredInputDto
                {
                    InputKey = input.InputKey,
                    Kind = input.Kind,
                    SourceId = input.SourceId,
                    SourceType = input.SourceType,
                    Name = input.Name,
                    FieldName = input.FieldName,
                    ValueType = input.ValueType,
                    HasSourceValue = input.HasSourceValue,
                    IsRequired = input.IsRequired,
                    SourceScopes = input.SourceScopes.ToDictionary(pair => pair.Key, pair => pair.Value.ToList())
                };

        private static OctopusImportIdMappingDto CloneMapping(OctopusImportIdMappingDto mapping)
            => mapping == null
                ? null
                : new OctopusImportIdMappingDto
                {
                    SourceId = mapping.SourceId,
                    SourceType = mapping.SourceType,
                    SourceName = mapping.SourceName,
                    DestinationType = mapping.DestinationType,
                    DestinationId = mapping.DestinationId,
                    OutcomeState = mapping.OutcomeState
                };

        private static OctopusImportDiagnosticDto CloneDiagnostic(OctopusImportDiagnosticDto diagnostic)
            => diagnostic == null
                ? null
                : new OctopusImportDiagnosticDto
                {
                    Severity = diagnostic.Severity,
                    Code = diagnostic.Code,
                    Message = diagnostic.Message,
                    ResourceType = diagnostic.ResourceType,
                    SourceId = diagnostic.SourceId,
                    ResourceName = diagnostic.ResourceName
                };
    }

    private sealed class TestOctopusImportTransactionExecutor : IOctopusImportTransactionExecutor
    {
        public int ExecuteCalls { get; private set; }

        public bool ThrowAfterAction { get; set; }

        public bool UseGate { get; set; }

        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteInImportTransactionAsync(OctopusImportTransactionContext context, Func<OctopusImportTransactionContext, CancellationToken, Task> action, CancellationToken ct = default)
        {
            await ExecuteInImportTransactionAsync(context, async (tx, token) =>
            {
                await action(tx, token).ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);
        }

        public async Task<T> ExecuteInImportTransactionAsync<T>(OctopusImportTransactionContext context, Func<OctopusImportTransactionContext, CancellationToken, Task<T>> action, CancellationToken ct = default)
        {
            ExecuteCalls++;
            Entered.TrySetResult(true);

            if (UseGate)
                await Release.Task.ConfigureAwait(false);

            var result = await action(context, ct).ConfigureAwait(false);
            if (ThrowAfterAction)
                throw new InvalidOperationException("boom");

            return result;
        }
    }
}

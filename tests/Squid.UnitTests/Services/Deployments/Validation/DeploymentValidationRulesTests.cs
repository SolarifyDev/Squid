using System.Collections.Generic;
using System;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Core.Services.Deployments.Validation;
using Squid.Core.Services.Deployments.Validation.Rules;
using Squid.Message.Models.Deployments.Snapshots;

namespace Squid.UnitTests.Services.Deployments.Validation;

public class DeploymentValidationRulesTests
{
    [Fact]
    public async Task MachineSelectionConsistencyValidationRule_WithOverlap_AddsIssue()
    {
        var rule = new MachineSelectionConsistencyValidationRule();
        var report = new DeploymentValidationReport();
        var context = new DeploymentValidationContext
        {
            SpecificMachineIds = [1, 2],
            ExcludedMachineIds = [2, 3]
        };

        await rule.EvaluateAsync(context, report, CancellationToken.None);

        report.IsValid.ShouldBeFalse();
        report.Issues.ShouldContain(i => i.Code == DeploymentValidationIssueCode.MachineSelectionOverlap);
    }

    [Fact]
    public async Task QueueWindowDeploymentValidationRule_WithExpiryBeforeQueue_AddsIssue()
    {
        var now = DateTimeOffset.UtcNow;
        var rule = new QueueWindowDeploymentValidationRule();
        var report = new DeploymentValidationReport();
        var context = new DeploymentValidationContext
        {
            QueueTime = now.AddMinutes(5),
            QueueTimeExpiry = now.AddMinutes(4)
        };

        await rule.EvaluateAsync(context, report, CancellationToken.None);

        report.IsValid.ShouldBeFalse();
        report.Issues.ShouldContain(i => i.Code == DeploymentValidationIssueCode.QueueTimeExpiryBeforeQueueTime);
    }

    [Fact]
    public async Task SkipActionsValidationRule_WithUnknownAction_AddsIssue()
    {
        var (rule, report, context) = CreateSkipActionRuleContext(skipActionIds: [99]);

        await rule.EvaluateAsync(context, report, CancellationToken.None);

        report.IsValid.ShouldBeFalse();
        report.Issues.ShouldContain(i => i.Code == DeploymentValidationIssueCode.SkipActionNotFound);
    }

    [Fact]
    public async Task SkipActionsValidationRule_WhenAllRunnableActionsSkipped_AddsIssue()
    {
        var (rule, report, context) = CreateSkipActionRuleContext(skipActionIds: [10]);

        await rule.EvaluateAsync(context, report, CancellationToken.None);

        report.IsValid.ShouldBeFalse();
        report.Issues.ShouldContain(i => i.Code == DeploymentValidationIssueCode.AllRunnableActionsSkipped);
    }

    [Fact]
    public async Task SkipActionsValidationRule_WithoutSkipActions_DoesNothing()
    {
        var (rule, report, context) = CreateSkipActionRuleContext(skipActionIds: []);

        await rule.EvaluateAsync(context, report, CancellationToken.None);

        report.IsValid.ShouldBeTrue();
        report.Issues.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProjectAvailabilityValidationRule_WhenProjectDisabled_AddsIssue()
    {
        var releaseDataProvider = new Mock<IReleaseDataProvider>();
        var projectDataProvider = new Mock<IProjectDataProvider>();

        releaseDataProvider
            .Setup(x => x.GetReleaseByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Squid.Core.Persistence.Entities.Deployments.Release
            {
                Id = 1,
                ProjectId = 9
            });

        projectDataProvider
            .Setup(x => x.GetProjectByIdAsync(9, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Squid.Core.Persistence.Entities.Deployments.Project
            {
                Id = 9,
                Name = "Disabled Project",
                IsDisabled = true
            });

        var rule = new ProjectAvailabilityValidationRule(releaseDataProvider.Object, projectDataProvider.Object);
        var report = new DeploymentValidationReport();

        await rule.EvaluateAsync(
            new DeploymentValidationContext { ReleaseId = 1, EnvironmentId = 2 },
            report,
            CancellationToken.None);

        report.IsValid.ShouldBeFalse();
        report.Issues.ShouldContain(i => i.Code == DeploymentValidationIssueCode.ProjectDisabled);
    }

    [Fact]
    public async Task FeedAvailabilityValidationRule_WithMissingFeed_AddsIssue()
    {
        var releaseDataProvider = new Mock<IReleaseDataProvider>();
        var snapshotService = new Mock<IDeploymentSnapshotService>();
        var feedDataProvider = new Mock<IExternalFeedDataProvider>();

        releaseDataProvider
            .Setup(x => x.GetReleaseByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Squid.Core.Persistence.Entities.Deployments.Release
            {
                Id = 1,
                ChannelId = 2,
                ProjectDeploymentProcessSnapshotId = 100
            });

        snapshotService
            .Setup(x => x.LoadProcessSnapshotAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSnapshotWithContainerFeed(88));

        feedDataProvider
            .Setup(x => x.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Squid.Core.Persistence.Entities.Deployments.ExternalFeed>());

        var rule = new FeedAvailabilityValidationRule(
            releaseDataProvider.Object,
            snapshotService.Object,
            feedDataProvider.Object);
        var report = new DeploymentValidationReport();

        await rule.EvaluateAsync(
            new DeploymentValidationContext { ReleaseId = 1, EnvironmentId = 3 },
            report,
            CancellationToken.None);

        report.IsValid.ShouldBeFalse();
        report.Issues.ShouldContain(i => i.Code == DeploymentValidationIssueCode.FeedNotFound);
    }

    [Fact]
    public async Task FeedAvailabilityValidationRule_WithExistingFeed_Passes()
    {
        var releaseDataProvider = new Mock<IReleaseDataProvider>();
        var snapshotService = new Mock<IDeploymentSnapshotService>();
        var feedDataProvider = new Mock<IExternalFeedDataProvider>();

        releaseDataProvider
            .Setup(x => x.GetReleaseByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Squid.Core.Persistence.Entities.Deployments.Release
            {
                Id = 1,
                ChannelId = 2,
                ProjectDeploymentProcessSnapshotId = 100
            });

        snapshotService
            .Setup(x => x.LoadProcessSnapshotAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSnapshotWithContainerFeed(88));

        feedDataProvider
            .Setup(x => x.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new Squid.Core.Persistence.Entities.Deployments.ExternalFeed
                {
                    Id = 88,
                    FeedType = "Docker Container Registry",
                    FeedUri = "https://index.docker.io/v2"
                }
            ]);

        var rule = new FeedAvailabilityValidationRule(
            releaseDataProvider.Object,
            snapshotService.Object,
            feedDataProvider.Object);
        var report = new DeploymentValidationReport();

        await rule.EvaluateAsync(
            new DeploymentValidationContext { ReleaseId = 1, EnvironmentId = 3 },
            report,
            CancellationToken.None);

        report.IsValid.ShouldBeTrue();
        report.Issues.ShouldBeEmpty();
    }

    private static (SkipActionsValidationRule Rule, DeploymentValidationReport Report, DeploymentValidationContext Context) CreateSkipActionRuleContext(
        HashSet<int> skipActionIds)
    {
        var releaseDataProvider = new Mock<IReleaseDataProvider>();
        var snapshotService = new Mock<IDeploymentSnapshotService>();

        releaseDataProvider
            .Setup(x => x.GetReleaseByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Squid.Core.Persistence.Entities.Deployments.Release
            {
                Id = 1,
                ChannelId = 2,
                ProjectDeploymentProcessSnapshotId = 100
            });

        snapshotService
            .Setup(x => x.LoadProcessSnapshotAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSnapshotWithSingleRunnableAction());

        var rule = new SkipActionsValidationRule(releaseDataProvider.Object, snapshotService.Object);
        var report = new DeploymentValidationReport();
        var context = new DeploymentValidationContext
        {
            ReleaseId = 1,
            EnvironmentId = 3,
            SkipActionIds = skipActionIds
        };

        return (rule, report, context);
    }

    private static DeploymentProcessSnapshotDto BuildSnapshotWithSingleRunnableAction()
    {
        return new DeploymentProcessSnapshotDto
        {
            Id = 100,
            Data = new DeploymentProcessSnapshotDataDto
            {
                StepSnapshots =
                [
                    new DeploymentStepSnapshotDataDto
                    {
                        Id = 1,
                        StepOrder = 1,
                        Name = "Deploy",
                        StepType = "Action",
                        Condition = "Success",
                        IsDisabled = false,
                        IsRequired = true,
                        ActionSnapshots =
                        [
                            new DeploymentActionSnapshotDataDto
                            {
                                Id = 10,
                                Name = "Run Script",
                                ActionType = "Octopus.Script",
                                ActionOrder = 1,
                                IsDisabled = false,
                                IsRequired = true
                            }
                        ]
                    }
                ]
            }
        };
    }

    private static DeploymentProcessSnapshotDto BuildSnapshotWithContainerFeed(int feedId)
    {
        return new DeploymentProcessSnapshotDto
        {
            Id = 100,
            Data = new DeploymentProcessSnapshotDataDto
            {
                StepSnapshots =
                [
                    new DeploymentStepSnapshotDataDto
                    {
                        Id = 1,
                        StepOrder = 1,
                        Name = "Deploy",
                        StepType = "Action",
                        Condition = "Success",
                        IsDisabled = false,
                        IsRequired = true,
                        ActionSnapshots =
                        [
                            new DeploymentActionSnapshotDataDto
                            {
                                Id = 10,
                                Name = "Deploy Container",
                                ActionType = "Squid.KubernetesDeployContainers",
                                ActionOrder = 1,
                                IsDisabled = false,
                                IsRequired = true,
                                Properties = new Dictionary<string, string>
                                {
                                    [
                                        "Squid.Action.KubernetesContainers.Containers"
                                    ] = $"[{{\"Name\":\"web\",\"PackageId\":\"nginx\",\"FeedId\":{feedId}}}]"
                                }
                            }
                        ]
                    }
                ]
            }
        };
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Shouldly;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;
using Xunit;

namespace Squid.UnitTests.Services.Deployments;

public class DeploymentVariableResolverTests
{
    private readonly Mock<IProjectDataProvider> _projectDataProvider = new();
    private readonly Mock<IDeploymentDataProvider> _deploymentDataProvider = new();
    private readonly Mock<IDeploymentSnapshotService> _snapshotService = new();
    private readonly DeploymentVariableResolver _resolver;

    public DeploymentVariableResolverTests()
    {
        _resolver = new DeploymentVariableResolver(
            _projectDataProvider.Object,
            _deploymentDataProvider.Object,
            _snapshotService.Object);
    }

    // === ResolveVariablesAsync — Snapshot Path ===

    [Fact]
    public async Task ResolveVariablesAsync_HasSnapshotId_LoadsFromSnapshot()
    {
        var deployment = CreateDeployment(variableSetSnapshotId: 42);
        var expectedVars = new List<VariableDto> { new() { Name = "Env", Value = "Production" } };

        _deploymentDataProvider
            .Setup(d => d.GetDeploymentByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        _snapshotService
            .Setup(s => s.LoadVariableSetSnapshotAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VariableSetSnapshotDto { Id = 42, Data = new VariableSetSnapshotDataDto { Variables = expectedVars } });

        var result = await _resolver.ResolveVariablesAsync(1, CancellationToken.None);

        result.ShouldBe(expectedVars);
        _snapshotService.Verify(s => s.LoadVariableSetSnapshotAsync(42, It.IsAny<CancellationToken>()), Times.Once);
        _snapshotService.Verify(s => s.SnapshotVariableSetFromIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveVariablesAsync_HasSnapshotId_DoesNotAccessProject()
    {
        var deployment = CreateDeployment(variableSetSnapshotId: 10);

        _deploymentDataProvider
            .Setup(d => d.GetDeploymentByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        _snapshotService
            .Setup(s => s.LoadVariableSetSnapshotAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VariableSetSnapshotDto { Data = new VariableSetSnapshotDataDto { Variables = new List<VariableDto>() } });

        await _resolver.ResolveVariablesAsync(1, CancellationToken.None);

        _projectDataProvider.Verify(p => p.GetProjectByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // === ResolveVariablesAsync — Fallback (No Snapshot) ===

    [Fact]
    public async Task ResolveVariablesAsync_NoSnapshotId_CreatesSnapshotFromProject()
    {
        var deployment = CreateDeployment(variableSetSnapshotId: null, projectId: 5);
        var project = CreateProject(id: 5, variableSetId: 100);
        var expectedVars = new List<VariableDto> { new() { Name = "AppName", Value = "Squid" } };

        _deploymentDataProvider
            .Setup(d => d.GetDeploymentByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        _projectDataProvider
            .Setup(p => p.GetProjectByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        _snapshotService
            .Setup(s => s.SnapshotVariableSetFromIdsAsync(
                It.Is<List<int>>(ids => ids.Contains(100)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VariableSetSnapshotDto { Id = 77, Data = new VariableSetSnapshotDataDto { Variables = expectedVars } });

        var result = await _resolver.ResolveVariablesAsync(1, CancellationToken.None);

        result.ShouldBe(expectedVars);
    }

    [Fact]
    public async Task ResolveVariablesAsync_NoSnapshotId_SavesSnapshotIdToDeployment()
    {
        var deployment = CreateDeployment(variableSetSnapshotId: null, projectId: 5);
        var project = CreateProject(id: 5, variableSetId: 100);

        _deploymentDataProvider
            .Setup(d => d.GetDeploymentByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        _projectDataProvider
            .Setup(p => p.GetProjectByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        _snapshotService
            .Setup(s => s.SnapshotVariableSetFromIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VariableSetSnapshotDto { Id = 77, Data = new VariableSetSnapshotDataDto { Variables = new List<VariableDto>() } });

        await _resolver.ResolveVariablesAsync(1, CancellationToken.None);

        deployment.VariableSetSnapshotId.ShouldBe(77);
        _deploymentDataProvider.Verify(d => d.UpdateDeploymentAsync(deployment, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveVariablesAsync_NoSnapshotId_IncludesLibraryVariableSetIds()
    {
        var deployment = CreateDeployment(variableSetSnapshotId: null, projectId: 5);
        var project = CreateProject(id: 5, variableSetId: 100, includedLibraryVariableSetIds: "200,300");
        List<int> capturedIds = null;

        _deploymentDataProvider
            .Setup(d => d.GetDeploymentByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        _projectDataProvider
            .Setup(p => p.GetProjectByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        _snapshotService
            .Setup(s => s.SnapshotVariableSetFromIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .Callback<List<int>, CancellationToken>((ids, _) => capturedIds = ids)
            .ReturnsAsync(new VariableSetSnapshotDto { Id = 1, Data = new VariableSetSnapshotDataDto { Variables = new List<VariableDto>() } });

        await _resolver.ResolveVariablesAsync(1, CancellationToken.None);

        capturedIds.ShouldNotBeNull();
        capturedIds.ShouldContain(200);
        capturedIds.ShouldContain(300);
        capturedIds.ShouldContain(100); // project's own VariableSetId
    }

    [Fact]
    public async Task ResolveVariablesAsync_NoSnapshotId_EmptyLibraryIds_UsesProjectVariableSetId()
    {
        var deployment = CreateDeployment(variableSetSnapshotId: null, projectId: 5);
        var project = CreateProject(id: 5, variableSetId: 100, includedLibraryVariableSetIds: "");
        List<int> capturedIds = null;

        _deploymentDataProvider
            .Setup(d => d.GetDeploymentByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        _projectDataProvider
            .Setup(p => p.GetProjectByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        _snapshotService
            .Setup(s => s.SnapshotVariableSetFromIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .Callback<List<int>, CancellationToken>((ids, _) => capturedIds = ids)
            .ReturnsAsync(new VariableSetSnapshotDto { Id = 1, Data = new VariableSetSnapshotDataDto { Variables = new List<VariableDto>() } });

        await _resolver.ResolveVariablesAsync(1, CancellationToken.None);

        capturedIds.ShouldNotBeNull();
        capturedIds.Count.ShouldBe(1);
        capturedIds.ShouldContain(100);
    }

    // === Error Cases ===

    [Fact]
    public async Task ResolveVariablesAsync_DeploymentNotFound_Throws()
    {
        _deploymentDataProvider
            .Setup(d => d.GetDeploymentByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Deployment)null);

        var ex = await Should.ThrowAsync<DeploymentEntityNotFoundException>(
            () => _resolver.ResolveVariablesAsync(999, CancellationToken.None));

        ex.Message.ShouldContain("999");
    }

    [Fact]
    public async Task ResolveVariablesAsync_ProjectNotFound_Throws()
    {
        var deployment = CreateDeployment(variableSetSnapshotId: null, projectId: 5);

        _deploymentDataProvider
            .Setup(d => d.GetDeploymentByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        _projectDataProvider
            .Setup(p => p.GetProjectByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Project)null);

        var ex = await Should.ThrowAsync<DeploymentEntityNotFoundException>(
            () => _resolver.ResolveVariablesAsync(1, CancellationToken.None));

        ex.Message.ShouldContain("5");
    }

    // === Helpers ===

    private static Deployment CreateDeployment(int? variableSetSnapshotId, int id = 1, int projectId = 1)
    {
        return new Deployment
        {
            Id = id,
            ProjectId = projectId,
            VariableSetSnapshotId = variableSetSnapshotId,
            Name = "Test Deployment",
            SpaceId = 1,
            ReleaseId = 1,
            EnvironmentId = 1,
            DeployedBy = 1,
            Created = DateTimeOffset.UtcNow
        };
    }

    private static Project CreateProject(
        int id = 1,
        int variableSetId = 1,
        string includedLibraryVariableSetIds = "")
    {
        return new Project
        {
            Id = id,
            Name = "Test Project",
            Slug = "test-project",
            VariableSetId = variableSetId,
            IncludedLibraryVariableSetIds = includedLibraryVariableSetIds,
            Json = string.Empty,
            DataVersion = Array.Empty<byte>(),
            SpaceId = 1,
            LastModified = DateTimeOffset.UtcNow
        };
    }
}

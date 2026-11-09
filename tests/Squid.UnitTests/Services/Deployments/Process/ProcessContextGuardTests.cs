using System.Linq;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Deployments.Process;
using Squid.Core.Services.Identity;
using Squid.Message.Enums;
using SquidProject = Squid.Core.Persistence.Entities.Deployments.Project;

namespace Squid.UnitTests.Services.Deployments.Process;

public class ProcessContextGuardTests
{
    private readonly Mock<IRepository> _repository = new();
    private readonly Mock<IAuthorizationService> _authorizationService = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly ProcessContextGuard _guard;

    public ProcessContextGuardTests()
    {
        _currentUser.Setup(user => user.Id).Returns(42);
        _currentUser.Setup(user => user.IsInternal).Returns(false);
        _guard = new ProcessContextGuard(
            _repository.Object,
            _authorizationService.Object,
            _currentUser.Object);
    }

    [Fact]
    public async Task ResolveForWriteAsync_RunbookProcess_UsesRunbookEditAndPersistedScope()
    {
        var project = Project(10, 20, 40);
        var runbook = Runbook(30, 10, 20, 40);
        var process = Process(40, 10, 20, DeploymentProcessKind.Runbook, 30);

        SetupQuery(process, project, runbook);

        var context = await _guard.ResolveForWriteAsync(
            new ProcessContextRequest(40, 20, 10, 30));

        context.Kind.ShouldBe(ProcessContextKind.Runbook);
        context.RunbookId.ShouldBe(30);
        context.ProjectId.ShouldBe(10);
        context.SpaceId.ShouldBe(20);

        _authorizationService.Verify(service => service.EnsurePermissionAsync(
            It.Is<PermissionCheckRequest>(request =>
                request.UserId == 42
                && request.Permission == Permission.RunbookEdit
                && request.ProjectId == 10
                && request.SpaceId == 20),
            It.IsAny<CancellationToken>()), Times.Once);
        _authorizationService.Verify(service => service.EnsurePermissionAsync(
            It.Is<PermissionCheckRequest>(request => request.Permission == Permission.ProcessEdit),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveForReadAsync_ProjectProcess_UsesProcessView()
    {
        var project = Project(10, 20, 40);
        var process = Process(40, 10, 20, DeploymentProcessKind.ProjectDeployment, null);

        SetupQuery(process, project);

        var context = await _guard.ResolveForReadAsync(
            new ProcessContextRequest(40, 20, 10));

        context.Kind.ShouldBe(ProcessContextKind.ProjectDeployment);
        _authorizationService.Verify(service => service.EnsurePermissionAsync(
            It.Is<PermissionCheckRequest>(request =>
                request.Permission == Permission.ProcessView
                && request.ProjectId == 10
                && request.SpaceId == 20),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveForWriteAsync_RunbookProcessWithoutExplicitRunbookContext_IsRejected()
    {
        var project = Project(10, 20, 40);
        var process = Process(40, 10, 20, DeploymentProcessKind.Runbook, 30);

        SetupQuery(process, project);

        await Should.ThrowAsync<ProcessContextMismatchException>(() =>
            _guard.ResolveForWriteAsync(new ProcessContextRequest(40, 20, 10)));

        _authorizationService.Verify(service => service.EnsurePermissionAsync(
            It.IsAny<PermissionCheckRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveForWriteAsync_WrongRunbookForProcess_IsRejected()
    {
        var project = Project(10, 20, 40);
        var process = Process(40, 10, 20, DeploymentProcessKind.Runbook, 30);
        var anotherRunbook = Runbook(31, 10, 20, 41);

        SetupQuery(process, project, anotherRunbook);

        await Should.ThrowAsync<ProcessContextMismatchException>(() =>
            _guard.ResolveForWriteAsync(new ProcessContextRequest(40, 20, 10, 31)));

        _authorizationService.Verify(service => service.EnsurePermissionAsync(
            It.IsAny<PermissionCheckRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveForWriteAsync_WrongProjectOrSpace_IsRejected()
    {
        var project = Project(10, 20, 40);
        var process = Process(40, 10, 20, DeploymentProcessKind.ProjectDeployment, null);

        SetupQuery(process, project);

        await Should.ThrowAsync<ProcessContextMismatchException>(() =>
            _guard.ResolveForWriteAsync(new ProcessContextRequest(40, 20, 11)));

        await Should.ThrowAsync<ProcessContextMismatchException>(() =>
            _guard.ResolveForWriteAsync(new ProcessContextRequest(40, 21, 10)));
    }

    [Fact]
    public async Task ResolveForWriteAsync_ProjectProcessNotLinkedFromProject_IsRejected()
    {
        var project = Project(10, 20, 41);
        var process = Process(40, 10, 20, DeploymentProcessKind.ProjectDeployment, null);

        SetupQuery(process, project);

        await Should.ThrowAsync<ProcessContextMismatchException>(() =>
            _guard.ResolveForWriteAsync(new ProcessContextRequest(40, 20, 10)));

        _authorizationService.Verify(service => service.EnsurePermissionAsync(
            It.IsAny<PermissionCheckRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveProjectDeploymentAsync_RunbookProcess_IsRejectedAsDeploymentProcess()
    {
        var project = Project(10, 20, 40);
        var runbook = Runbook(30, 10, 20, 40);
        var process = Process(40, 10, 20, DeploymentProcessKind.Runbook, 30);

        SetupQuery(process, project, runbook);

        await Should.ThrowAsync<ProcessContextMismatchException>(() =>
            _guard.ResolveProjectDeploymentAsync(40, 10, 20));
    }

    [Fact]
    public async Task EnsurePermissionAsync_InternalUser_UsesNoPermissionLookup()
    {
        _currentUser.Setup(user => user.IsInternal).Returns(true);
        var context = new ProcessContext(
            40,
            10,
            20,
            ProcessContextKind.Runbook,
            30);

        await _guard.EnsurePermissionAsync(context, write: true);

        _authorizationService.Verify(service => service.EnsurePermissionAsync(
            It.IsAny<PermissionCheckRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private void SetupQuery(
        DeploymentProcess process,
        SquidProject project = null,
        Runbook runbook = null)
    {
        _repository.Setup(repository => repository.QueryNoTracking<DeploymentProcess>(
                It.IsAny<System.Linq.Expressions.Expression<Func<DeploymentProcess, bool>>>()))
            .Returns(new[] { process }.AsQueryable().BuildMock());

        _repository.Setup(repository => repository.QueryNoTracking<SquidProject>(
                It.IsAny<System.Linq.Expressions.Expression<Func<SquidProject, bool>>>()))
            .Returns(project == null
                ? Array.Empty<SquidProject>().AsQueryable().BuildMock()
                : new[] { project }.AsQueryable().BuildMock());

        _repository.Setup(repository => repository.QueryNoTracking<Runbook>(
                It.IsAny<System.Linq.Expressions.Expression<Func<Runbook, bool>>>()))
            .Returns(runbook == null
                ? Array.Empty<Runbook>().AsQueryable().BuildMock()
                : new[] { runbook }.AsQueryable().BuildMock());
    }

    private static SquidProject Project(int id, int spaceId, int deploymentProcessId)
    {
        return new SquidProject
        {
            Id = id,
            SpaceId = spaceId,
            DeploymentProcessId = deploymentProcessId,
            Name = $"Project {id}",
            Slug = $"project-{id}"
        };
    }

    private static Runbook Runbook(int id, int projectId, int spaceId, int processId)
    {
        return new Runbook
        {
            Id = id,
            ProjectId = projectId,
            SpaceId = spaceId,
            Name = $"Runbook {id}",
            ProcessId = processId,
            VariableSetId = id + 100
        };
    }

    private static DeploymentProcess Process(
        int id,
        int projectId,
        int spaceId,
        DeploymentProcessKind kind,
        int? ownerId)
    {
        return new DeploymentProcess
        {
            Id = id,
            ProjectId = projectId,
            SpaceId = spaceId,
            ProcessKind = kind,
            OwnerId = ownerId
        };
    }
}

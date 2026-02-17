using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments;
using Squid.Core.Services.Deployments.Machine;
using Squid.Message.Enums;
using Machine = Squid.Core.Persistence.Entities.Deployments.Machine;

namespace Squid.UnitTests.Services.Deployments;

public class DeploymentTargetFinderTests
{
    private readonly Mock<IMachineDataProvider> _machineDataProviderMock = new();
    private readonly DeploymentTargetFinder _finder;

    public DeploymentTargetFinderTests()
    {
        _finder = new DeploymentTargetFinder(_machineDataProviderMock.Object);
    }

    // === Helpers ===

    private static Machine CreateMachine(
        int id,
        string name = null,
        bool disabled = false,
        string envIds = "1",
        string roles = "web",
        string endpoint = "{}") => new()
    {
        Id = id,
        Name = name ?? $"Machine-{id}",
        IsDisabled = disabled,
        EnvironmentIds = envIds,
        Roles = roles,
        SpaceId = 1,
        Endpoint = endpoint,
        Uri = $"https://machine{id}:10933",
        Thumbprint = $"THUMB-{id}",
        OperatingSystem = OperatingSystemType.Linux,
        Slug = $"machine-{id}"
    };

    private static Deployment CreateDeployment(
        int environmentId = 1,
        int machineId = 0) => new()
    {
        Id = 1,
        Name = "Test Deployment",
        EnvironmentId = environmentId,
        MachineId = machineId,
        SpaceId = 1,
        Created = DateTimeOffset.UtcNow
    };

    private void SetupGetById(int id, Machine machine)
    {
        _machineDataProviderMock
            .Setup(p => p.GetMachinesByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(machine);
    }

    private void SetupGetByFilter(List<Machine> machines)
    {
        _machineDataProviderMock
            .Setup(p => p.GetMachinesByFilterAsync(
                It.IsAny<HashSet<int>>(),
                It.IsAny<HashSet<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(machines);
    }

    // ============================
    // Specific Machine Mode (MachineId > 0)
    // Octopus equivalent: SpecificMachineIds
    // ============================

    [Fact]
    public async Task SpecificMachine_ValidEnabledCorrectEnv_ReturnsMachine()
    {
        var machine = CreateMachine(10, envIds: "1");
        SetupGetById(10, machine);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1, machineId: 10), CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(10);
    }

    [Fact]
    public async Task SpecificMachine_Disabled_ReturnsEmpty()
    {
        var machine = CreateMachine(10, disabled: true, envIds: "1");
        SetupGetById(10, machine);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1, machineId: 10), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SpecificMachine_WrongEnvironment_ReturnsEmpty()
    {
        var machine = CreateMachine(10, envIds: "2");
        SetupGetById(10, machine);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1, machineId: 10), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SpecificMachine_NotFound_ReturnsEmpty()
    {
        SetupGetById(99, null);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1, machineId: 99), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SpecificMachine_CommaSeparatedEnvIds_MatchesCorrectly()
    {
        var machine = CreateMachine(10, envIds: "3,1,5");
        SetupGetById(10, machine);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1, machineId: 10), CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(10);
    }

    [Fact]
    public async Task SpecificMachine_EmptyEnvironmentIds_Excluded()
    {
        var machine = CreateMachine(10, envIds: "");
        SetupGetById(10, machine);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1, machineId: 10), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SpecificMachine_NullEnvironmentIds_Excluded()
    {
        var machine = CreateMachine(10, envIds: null);
        SetupGetById(10, machine);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1, machineId: 10), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SpecificMachine_DisabledAndWrongEnv_ReturnsEmpty()
    {
        var machine = CreateMachine(10, disabled: true, envIds: "2");
        SetupGetById(10, machine);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1, machineId: 10), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    // ============================
    // Auto-Select Mode (MachineId == 0)
    // Octopus equivalent: no SpecificMachineIds, select all by Environment + Role
    // ============================

    [Fact]
    public async Task AutoSelect_SingleMachineInEnv_ReturnsIt()
    {
        var machines = new List<Machine> { CreateMachine(1, envIds: "1") };
        SetupGetByFilter(machines);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1), CancellationToken.None);

        result.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AutoSelect_MultipleMachinesInEnv_ReturnsAll()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, envIds: "1"),
            CreateMachine(2, envIds: "1"),
            CreateMachine(3, envIds: "1")
        };
        SetupGetByFilter(machines);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1), CancellationToken.None);

        result.Count.ShouldBe(3);
    }

    [Fact]
    public async Task AutoSelect_ExcludesDisabledMachines()
    {
        // Simulates case where DB query returns a disabled machine (data inconsistency)
        var machines = new List<Machine>
        {
            CreateMachine(1, envIds: "1"),
            CreateMachine(2, disabled: true, envIds: "1"),
            CreateMachine(3, envIds: "1")
        };
        SetupGetByFilter(machines);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1), CancellationToken.None);

        result.Count.ShouldBe(2);
        result.ShouldNotContain(m => m.Id == 2);
    }

    [Fact]
    public async Task AutoSelect_FiltersWrongEnvironment()
    {
        // DB returns machines from multiple envs (data inconsistency defense)
        var machines = new List<Machine>
        {
            CreateMachine(1, envIds: "1"),
            CreateMachine(2, envIds: "2"),
            CreateMachine(3, envIds: "1,3")
        };
        SetupGetByFilter(machines);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1), CancellationToken.None);

        result.Count.ShouldBe(2);
        result.ShouldContain(m => m.Id == 1);
        result.ShouldContain(m => m.Id == 3);
        result.ShouldNotContain(m => m.Id == 2);
    }

    [Fact]
    public async Task AutoSelect_NoMachinesInEnv_ReturnsEmpty()
    {
        SetupGetByFilter(new List<Machine>());

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task AutoSelect_CallsProviderWithCorrectEnvironmentId()
    {
        SetupGetByFilter(new List<Machine>());

        await _finder.FindTargetsAsync(CreateDeployment(environmentId: 42), CancellationToken.None);

        _machineDataProviderMock.Verify(p => p.GetMachinesByFilterAsync(
            It.Is<HashSet<int>>(ids => ids.Contains(42) && ids.Count == 1),
            It.IsAny<HashSet<string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AutoSelect_DoesNotCallGetByIdWhenMachineIdIsZero()
    {
        SetupGetByFilter(new List<Machine>());

        await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1, machineId: 0), CancellationToken.None);

        _machineDataProviderMock.Verify(
            p => p.GetMachinesByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SpecificMachine_DoesNotCallGetByFilter()
    {
        SetupGetById(10, CreateMachine(10, envIds: "1"));

        await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1, machineId: 10), CancellationToken.None);

        _machineDataProviderMock.Verify(
            p => p.GetMachinesByFilterAsync(It.IsAny<HashSet<int>>(), It.IsAny<HashSet<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================
    // Environment ID Parsing (static utility)
    // ============================

    [Fact]
    public void ParseIds_CommaSeparated_ReturnsCorrectSet()
    {
        var result = DeploymentTargetFinder.ParseIds("1,2,3");

        result.Count.ShouldBe(3);
        result.ShouldContain(1);
        result.ShouldContain(2);
        result.ShouldContain(3);
    }

    [Fact]
    public void ParseIds_SingleValue_ReturnsSingleSet()
    {
        var result = DeploymentTargetFinder.ParseIds("42");

        result.Count.ShouldBe(1);
        result.ShouldContain(42);
    }

    [Fact]
    public void ParseIds_EmptyString_ReturnsEmptySet()
    {
        DeploymentTargetFinder.ParseIds("").ShouldBeEmpty();
    }

    [Fact]
    public void ParseIds_Null_ReturnsEmptySet()
    {
        DeploymentTargetFinder.ParseIds(null).ShouldBeEmpty();
    }

    [Fact]
    public void ParseIds_InvalidValues_IgnoresNonNumeric()
    {
        var result = DeploymentTargetFinder.ParseIds("1,abc,3");

        result.Count.ShouldBe(2);
        result.ShouldContain(1);
        result.ShouldContain(3);
    }

    [Fact]
    public void ParseIds_WithWhitespace_TrimsCorrectly()
    {
        var result = DeploymentTargetFinder.ParseIds(" 1 , 2 , 3 ");

        result.Count.ShouldBe(3);
        result.ShouldContain(1);
        result.ShouldContain(2);
        result.ShouldContain(3);
    }

    [Fact]
    public void ParseIds_DuplicateValues_ReturnsUniqueSet()
    {
        var result = DeploymentTargetFinder.ParseIds("1,1,2,2");

        result.Count.ShouldBe(2);
    }

    // ============================
    // Role Parsing (static utility for per-step filtering)
    // ============================

    [Fact]
    public void ParseRoles_CommaSeparated_ReturnsCorrectSet()
    {
        var result = DeploymentTargetFinder.ParseRoles("web,api,worker");

        result.Count.ShouldBe(3);
        result.ShouldContain("web");
        result.ShouldContain("api");
        result.ShouldContain("worker");
    }

    [Fact]
    public void ParseRoles_SingleRole_ReturnsSingleSet()
    {
        var result = DeploymentTargetFinder.ParseRoles("web");

        result.Count.ShouldBe(1);
        result.ShouldContain("web");
    }

    [Fact]
    public void ParseRoles_EmptyString_ReturnsEmptySet()
    {
        DeploymentTargetFinder.ParseRoles("").ShouldBeEmpty();
    }

    [Fact]
    public void ParseRoles_Null_ReturnsEmptySet()
    {
        DeploymentTargetFinder.ParseRoles(null).ShouldBeEmpty();
    }

    [Fact]
    public void ParseRoles_CaseInsensitive()
    {
        var result = DeploymentTargetFinder.ParseRoles("Web,API");

        result.Contains("web").ShouldBeTrue();
        result.Contains("api").ShouldBeTrue();
        result.Contains("WEB").ShouldBeTrue();
    }

    [Fact]
    public void ParseRoles_WithWhitespace_TrimsCorrectly()
    {
        var result = DeploymentTargetFinder.ParseRoles(" web , api ");

        result.Count.ShouldBe(2);
        result.ShouldContain("web");
        result.ShouldContain("api");
    }

    // ============================
    // FilterByRoles (static utility for per-step filtering)
    // Octopus: step.TargetRoles → machines with ANY matching role (OR logic)
    // ============================

    [Fact]
    public void FilterByRoles_MatchingRole_ReturnsMachine()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, roles: "web,api"),
            CreateMachine(2, roles: "worker")
        };
        var targetRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        var result = DeploymentTargetFinder.FilterByRoles(machines, targetRoles);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(1);
    }

    [Fact]
    public void FilterByRoles_MultipleRoles_OrLogic_ReturnsAnyMatch()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, roles: "web"),
            CreateMachine(2, roles: "api"),
            CreateMachine(3, roles: "worker")
        };
        var targetRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web", "api" };

        var result = DeploymentTargetFinder.FilterByRoles(machines, targetRoles);

        result.Count.ShouldBe(2);
        result.ShouldContain(m => m.Id == 1);
        result.ShouldContain(m => m.Id == 2);
    }

    [Fact]
    public void FilterByRoles_NoMatchingRole_ReturnsEmpty()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, roles: "web"),
            CreateMachine(2, roles: "api")
        };
        var targetRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "database" };

        var result = DeploymentTargetFinder.FilterByRoles(machines, targetRoles);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void FilterByRoles_EmptyTargetRoles_ReturnsAll()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, roles: "web"),
            CreateMachine(2, roles: "api")
        };

        var result = DeploymentTargetFinder.FilterByRoles(machines, new HashSet<string>());

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void FilterByRoles_CaseInsensitiveMatch()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, roles: "Web,API")
        };
        var targetRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        var result = DeploymentTargetFinder.FilterByRoles(machines, targetRoles);

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void FilterByRoles_MachineWithEmptyRoles_Excluded()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, roles: ""),
            CreateMachine(2, roles: "web")
        };
        var targetRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        var result = DeploymentTargetFinder.FilterByRoles(machines, targetRoles);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(2);
    }

    [Fact]
    public void FilterByRoles_NullTargetRoles_ReturnsAll()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, roles: "web"),
            CreateMachine(2, roles: "api")
        };

        var result = DeploymentTargetFinder.FilterByRoles(machines, null);

        result.Count.ShouldBe(2);
    }

    // ============================
    // Edge Cases
    // ============================

    [Fact]
    public async Task FindTargets_NullDeployment_ThrowsArgumentNullException()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            () => _finder.FindTargetsAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task AutoSelect_MachineWithMultipleEnvs_OneMatchesTarget()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, envIds: "1,2,3"),
            CreateMachine(2, envIds: "4,5")
        };
        SetupGetByFilter(machines);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 2), CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(1);
    }

    [Fact]
    public async Task AutoSelect_AllDisabled_ReturnsEmpty()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, disabled: true, envIds: "1"),
            CreateMachine(2, disabled: true, envIds: "1")
        };
        SetupGetByFilter(machines);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SpecificMachine_EnvIdSubstringNoFalsePositive()
    {
        // Machine has envIds "11" — should NOT match environment 1
        var machine = CreateMachine(10, envIds: "11");
        SetupGetById(10, machine);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1, machineId: 10), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SpecificMachine_EnvId1InList11_NoFalsePositive()
    {
        // Machine has envIds "11,12" — should NOT match environment 1
        var machine = CreateMachine(10, envIds: "11,12");
        SetupGetById(10, machine);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1, machineId: 10), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SpecificMachine_EnvId11InList_CorrectMatch()
    {
        // Machine has envIds "1,11" — should match environment 11
        var machine = CreateMachine(10, envIds: "1,11");
        SetupGetById(10, machine);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 11, machineId: 10), CancellationToken.None);

        result.Count.ShouldBe(1);
    }
}

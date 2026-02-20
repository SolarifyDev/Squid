using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Machine;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;
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

    // ========== Helpers ==========

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

    private static DeploymentStepDto MakeStepWithRoles(string targetRoles, bool isDisabled = false)
    {
        var step = new DeploymentStepDto
        {
            Id = 1,
            StepOrder = 1,
            Name = "Test Step",
            StepType = "Action",
            Condition = "Success",
            IsDisabled = isDisabled,
            IsRequired = true,
            Properties = new List<DeploymentStepPropertyDto>()
        };

        if (targetRoles != null)
        {
            step.Properties.Add(new DeploymentStepPropertyDto
            {
                StepId = 1,
                PropertyName = DeploymentVariables.Action.TargetRoles,
                PropertyValue = targetRoles
            });
        }

        return step;
    }

    // ============================
    // Specific Machine Mode (MachineId > 0)
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

    // ============================
    // Auto-Select Mode (MachineId == 0)
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
    // ParseIds (static utility)
    // ============================

    [Theory]
    [InlineData("1,2,3", 3)]
    [InlineData("42", 1)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("1,abc,3", 2)]
    [InlineData("0,1,2", 2)]
    [InlineData("-1,1,-5,3", 2)]
    [InlineData(" 1 , 2 , 3 ", 3)]
    [InlineData("1,1,2,2", 2)]
    public void ParseIds_ReturnsCorrectCount(string input, int expectedCount)
    {
        var result = DeploymentTargetFinder.ParseIds(input);

        result.Count.ShouldBe(expectedCount);
    }

    [Fact]
    public void ParseIds_CorrectValues()
    {
        var result = DeploymentTargetFinder.ParseIds("1,abc,-5,0,3");

        result.ShouldContain(1);
        result.ShouldContain(3);
        result.ShouldNotContain(0);
    }

    // ============================
    // ParseRoles (static utility)
    // ============================

    [Theory]
    [InlineData("web,api,worker", 3)]
    [InlineData("web", 1)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData(" web , api ", 2)]
    [InlineData("web-server,api-gateway,k8s-worker", 3)]
    [InlineData("k8s.cluster,aws.ec2", 2)]
    [InlineData("web,Web,WEB,api,Api", 2)]
    [InlineData("web_server,api_gateway", 2)]
    public void ParseRoles_ReturnsCorrectCount(string input, int expectedCount)
    {
        var result = DeploymentTargetFinder.ParseRoles(input);

        result.Count.ShouldBe(expectedCount);
    }

    [Fact]
    public void ParseRoles_CaseInsensitiveLookup()
    {
        var result = DeploymentTargetFinder.ParseRoles("Web,API");

        result.Contains("web").ShouldBeTrue();
        result.Contains("api").ShouldBeTrue();
        result.Contains("WEB").ShouldBeTrue();
    }

    // ============================
    // FilterByRoles (static utility — OR logic)
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
    public void FilterByRoles_MachineWithNullRoles_Excluded()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, roles: null),
            CreateMachine(2, roles: "web")
        };
        var targetRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        var result = DeploymentTargetFinder.FilterByRoles(machines, targetRoles);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(2);
    }

    [Fact]
    public void FilterByRoles_MachineMultipleRoles_OneOverlaps_Included()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, roles: "web,api,database,cache")
        };
        var targetRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "database" };

        var result = DeploymentTargetFinder.FilterByRoles(machines, targetRoles);

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void FilterByRoles_RoleSubstring_NoFalsePositive()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, roles: "web"),
            CreateMachine(2, roles: "web-server")
        };
        var targetRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web-server" };

        var result = DeploymentTargetFinder.FilterByRoles(machines, targetRoles);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(2);
    }

    [Fact]
    public void FilterByRoles_RoleSubstringReverse_NoFalsePositive()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, roles: "web-server")
        };
        var targetRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        var result = DeploymentTargetFinder.FilterByRoles(machines, targetRoles);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void FilterByRoles_AllMachinesMatch_ReturnsAll()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, roles: "web,api"),
            CreateMachine(2, roles: "web"),
            CreateMachine(3, roles: "api,web")
        };
        var targetRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        var result = DeploymentTargetFinder.FilterByRoles(machines, targetRoles);

        result.Count.ShouldBe(3);
    }

    [Fact]
    public void FilterByRoles_ManyRolesOnBothSides_CorrectOverlap()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, roles: "web,api,cache"),
            CreateMachine(2, roles: "database,queue,scheduler"),
            CreateMachine(3, roles: "web,database")
        };
        var targetRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api", "queue" };

        var result = DeploymentTargetFinder.FilterByRoles(machines, targetRoles);

        result.Count.ShouldBe(2);
        result.ShouldContain(m => m.Id == 1); // has "api"
        result.ShouldContain(m => m.Id == 2); // has "queue"
    }

    [Fact]
    public void FilterByRoles_SpecialCharacters_MatchExactly()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, roles: "k8s-worker.us-east-1,aws_ec2"),
            CreateMachine(2, roles: "k8s-worker.eu-west-1")
        };
        var targetRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "k8s-worker.us-east-1" };

        var result = DeploymentTargetFinder.FilterByRoles(machines, targetRoles);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(1);
    }

    [Fact]
    public void FilterByRoles_WhitespaceInMachineRoles_TrimmedCorrectly()
    {
        var machines = new List<Machine>
        {
            CreateMachine(1, roles: " web , api ")
        };
        var targetRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        var result = DeploymentTargetFinder.FilterByRoles(machines, targetRoles);

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void FilterByRoles_EmptyMachineList_ReturnsEmpty()
    {
        var result = DeploymentTargetFinder.FilterByRoles(
            new List<Machine>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" });

        result.ShouldBeEmpty();
    }

    // ============================
    // CollectAllTargetRoles (Level 1 Pre-Filtering)
    // ============================

    [Fact]
    public void CollectAllTargetRoles_AllStepsHaveRoles_ReturnsUnion()
    {
        var steps = new List<DeploymentStepDto>
        {
            MakeStepWithRoles("web,api"),
            MakeStepWithRoles("database"),
            MakeStepWithRoles("api,cache")
        };

        var result = DeploymentTargetFinder.CollectAllTargetRoles(steps);

        result.Count.ShouldBe(4); // web, api, database, cache
        result.ShouldContain("web");
        result.ShouldContain("api");
        result.ShouldContain("database");
        result.ShouldContain("cache");
    }

    [Fact]
    public void CollectAllTargetRoles_OneStepHasNoRoles_ReturnsEmpty()
    {
        var steps = new List<DeploymentStepDto>
        {
            MakeStepWithRoles("web"),
            MakeStepWithRoles(null), // No roles → runs on all machines
            MakeStepWithRoles("api")
        };

        var result = DeploymentTargetFinder.CollectAllTargetRoles(steps);

        result.ShouldBeEmpty(); // Empty = no pre-filtering
    }

    [Fact]
    public void CollectAllTargetRoles_DisabledStepWithNoRoles_Ignored()
    {
        var steps = new List<DeploymentStepDto>
        {
            MakeStepWithRoles("web"),
            MakeStepWithRoles(null, isDisabled: true), // Disabled, no roles — ignored
            MakeStepWithRoles("api")
        };

        var result = DeploymentTargetFinder.CollectAllTargetRoles(steps);

        result.Count.ShouldBe(2);
        result.ShouldContain("web");
        result.ShouldContain("api");
    }

    [Fact]
    public void CollectAllTargetRoles_EmptyStepList_ReturnsEmpty()
    {
        var result = DeploymentTargetFinder.CollectAllTargetRoles(new List<DeploymentStepDto>());

        result.ShouldBeEmpty();
    }

    [Fact]
    public void CollectAllTargetRoles_NullStepList_ReturnsEmpty()
    {
        var result = DeploymentTargetFinder.CollectAllTargetRoles(null);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void CollectAllTargetRoles_AllStepsDisabled_ReturnsEmpty()
    {
        var steps = new List<DeploymentStepDto>
        {
            MakeStepWithRoles("web", isDisabled: true),
            MakeStepWithRoles("api", isDisabled: true)
        };

        var result = DeploymentTargetFinder.CollectAllTargetRoles(steps);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void CollectAllTargetRoles_DuplicateRoles_Deduplicated()
    {
        var steps = new List<DeploymentStepDto>
        {
            MakeStepWithRoles("web,api"),
            MakeStepWithRoles("Web,API") // Same roles, different case
        };

        var result = DeploymentTargetFinder.CollectAllTargetRoles(steps);

        result.Count.ShouldBe(2); // web, api (case-insensitive dedup)
    }

    [Fact]
    public void CollectAllTargetRoles_EmptyRolesValue_TreatedAsNoRoles()
    {
        var steps = new List<DeploymentStepDto>
        {
            MakeStepWithRoles("web"),
            MakeStepWithRoles("") // Empty = no filter
        };

        var result = DeploymentTargetFinder.CollectAllTargetRoles(steps);

        result.ShouldBeEmpty(); // Must load all machines
    }

    [Fact]
    public void CollectAllTargetRoles_SingleStep_ReturnsItsRoles()
    {
        var steps = new List<DeploymentStepDto>
        {
            MakeStepWithRoles("web-server,frontend")
        };

        var result = DeploymentTargetFinder.CollectAllTargetRoles(steps);

        result.Count.ShouldBe(2);
        result.ShouldContain("web-server");
        result.ShouldContain("frontend");
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
        var machine = CreateMachine(10, envIds: "11");
        SetupGetById(10, machine);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1, machineId: 10), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SpecificMachine_EnvId1InList11_NoFalsePositive()
    {
        var machine = CreateMachine(10, envIds: "11,12");
        SetupGetById(10, machine);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 1, machineId: 10), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SpecificMachine_EnvId11InList_CorrectMatch()
    {
        var machine = CreateMachine(10, envIds: "1,11");
        SetupGetById(10, machine);

        var result = await _finder.FindTargetsAsync(CreateDeployment(environmentId: 11, machineId: 10), CancellationToken.None);

        result.Count.ShouldBe(1);
    }
}

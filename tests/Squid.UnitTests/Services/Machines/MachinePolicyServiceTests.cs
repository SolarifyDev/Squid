using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Machines;

public class MachinePolicyServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    // ========================================================================
    // RPC Retry Policy — round-trip
    // ========================================================================

    [Fact]
    public void ToDto_DeserializesRpcCallRetryPolicy()
    {
        var rpcPolicy = new MachineRpcCallRetryPolicyDto
        {
            Enabled = false,
            DeploymentRetryDurationSeconds = 300,
            HealthCheckRetryDurationSeconds = 120
        };

        var entity = new MachinePolicy
        {
            Id = 1, SpaceId = 1, Name = "test",
            MachineRpcCallRetryPolicy = JsonSerializer.Serialize(rpcPolicy, JsonOptions)
        };

        var dto = MachinePolicyService.ToDto(entity);

        dto.MachineRpcCallRetryPolicy.Enabled.ShouldBeFalse();
        dto.MachineRpcCallRetryPolicy.DeploymentRetryDurationSeconds.ShouldBe(300);
        dto.MachineRpcCallRetryPolicy.HealthCheckRetryDurationSeconds.ShouldBe(120);
    }

    [Fact]
    public void ApplyDto_SerializesRpcCallRetryPolicy()
    {
        var dto = new MachinePolicyDto
        {
            SpaceId = 1, Name = "test",
            MachineRpcCallRetryPolicy = new MachineRpcCallRetryPolicyDto
            {
                Enabled = true,
                DeploymentRetryDurationSeconds = 200,
                HealthCheckRetryDurationSeconds = 100
            }
        };

        var entity = new MachinePolicy();
        MachinePolicyService.ApplyDto(entity, dto);

        entity.MachineRpcCallRetryPolicy.ShouldNotBeNullOrWhiteSpace();

        var deserialized = JsonSerializer.Deserialize<MachineRpcCallRetryPolicyDto>(entity.MachineRpcCallRetryPolicy, JsonOptions);

        deserialized.Enabled.ShouldBeTrue();
        deserialized.DeploymentRetryDurationSeconds.ShouldBe(200);
        deserialized.HealthCheckRetryDurationSeconds.ShouldBe(100);
    }

    // ========================================================================
    // ScriptPolicy key migration
    // ========================================================================

    [Theory]
    [InlineData("KubernetesApi", "Bash")]
    [InlineData("KubernetesAgent", "Bash")]
    [InlineData("Ssh", "Bash")]
    [InlineData("WindowsTentacle", "PowerShell")]
    public void MigrateScriptPolicyKeys_CommunicationStyleToScriptSyntax(string oldKey, string expectedKey)
    {
        var dto = new MachineHealthCheckPolicyDto
        {
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                [oldKey] = new() { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "echo test" }
            }
        };

        var migrated = MachinePolicyService.MigrateScriptPolicyKeys(dto);

        migrated.ScriptPolicies.ShouldContainKey(expectedKey);
        migrated.ScriptPolicies.ShouldNotContainKey(oldKey);
    }

    [Fact]
    public void MigrateScriptPolicyKeys_AlreadyScriptSyntax_PreservesKeys()
    {
        var dto = new MachineHealthCheckPolicyDto
        {
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                ["Bash"] = new() { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "echo test" }
            }
        };

        var migrated = MachinePolicyService.MigrateScriptPolicyKeys(dto);

        migrated.ScriptPolicies.ShouldContainKey("Bash");
        migrated.ScriptPolicies.Count.ShouldBe(1);
    }

    [Fact]
    public void MigrateScriptPolicyKeys_MultipleOldKeys_DeduplicatesByTryAdd()
    {
        var dto = new MachineHealthCheckPolicyDto
        {
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                ["KubernetesApi"] = new() { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "k8s-api script" },
                ["KubernetesAgent"] = new() { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "k8s-agent script" }
            }
        };

        var migrated = MachinePolicyService.MigrateScriptPolicyKeys(dto);

        migrated.ScriptPolicies.Count.ShouldBe(1);
        migrated.ScriptPolicies.ShouldContainKey("Bash");
    }

    [Fact]
    public void MigrateScriptPolicyKeys_NullScriptPolicies_ReturnsDto()
    {
        var dto = new MachineHealthCheckPolicyDto { ScriptPolicies = null };

        var result = MachinePolicyService.MigrateScriptPolicyKeys(dto);

        result.ShouldBe(dto);
        result.ScriptPolicies.ShouldBeNull();
    }

    [Fact]
    public void MigrateScriptPolicyKeys_EmptyScriptPolicies_ReturnsUnchanged()
    {
        var dto = new MachineHealthCheckPolicyDto { ScriptPolicies = new() };

        var result = MachinePolicyService.MigrateScriptPolicyKeys(dto);

        result.ScriptPolicies.ShouldBeEmpty();
    }

    [Fact]
    public void MigrateScriptPolicyKeys_MixedOldAndNewKeys_NewKeyTakesPriority()
    {
        var dto = new MachineHealthCheckPolicyDto
        {
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                ["KubernetesApi"] = new() { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "old script" },
                ["Bash"] = new() { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "new script" }
            }
        };

        var migrated = MachinePolicyService.MigrateScriptPolicyKeys(dto);

        migrated.ScriptPolicies.Count.ShouldBe(1);
        migrated.ScriptPolicies["Bash"].ScriptBody.ShouldBe("new script");
    }

    [Fact]
    public void MigrateScriptPolicyKeys_NullDto_ReturnsNull()
    {
        MachinePolicyService.MigrateScriptPolicyKeys(null).ShouldBeNull();
    }

    // ========================================================================
    // Connectivity Policy — full round-trip
    // ========================================================================

    [Fact]
    public void ToDto_ConnectivityPolicy_AllFields_RoundTrip()
    {
        var connectivityDto = new MachineConnectivityPolicyDto
        {
            MachineConnectivityBehavior = MachineConnectivityBehavior.MayBeOfflineAndCanBeSkipped,
            ConnectTimeoutSeconds = 30,
            RetryAttempts = 3,
            RetryWaitIntervalSeconds = 2,
            RetryTimeLimitSeconds = 120,
            PollingRequestQueueTimeoutSeconds = 300
        };

        var entity = new MachinePolicy
        {
            Id = 1, SpaceId = 1, Name = "test",
            MachineConnectivityPolicy = JsonSerializer.Serialize(connectivityDto, JsonOptions)
        };

        var dto = MachinePolicyService.ToDto(entity);

        dto.MachineConnectivityPolicy.MachineConnectivityBehavior.ShouldBe(MachineConnectivityBehavior.MayBeOfflineAndCanBeSkipped);
        dto.MachineConnectivityPolicy.ConnectTimeoutSeconds.ShouldBe(30);
        dto.MachineConnectivityPolicy.RetryAttempts.ShouldBe(3);
        dto.MachineConnectivityPolicy.PollingRequestQueueTimeoutSeconds.ShouldBe(300);
    }

    // ========================================================================
    // Update Policy — K8s agent behavior
    // ========================================================================

    [Fact]
    public void ToDto_UpdatePolicy_KubernetesAgentBehavior()
    {
        var updateDto = new MachineUpdatePolicyDto
        {
            CalamariUpdateBehavior = CalamariUpdateBehavior.UpdateAlways,
            TentacleUpdateBehavior = AgentUpdateBehavior.Update,
            KubernetesAgentUpdateBehavior = AgentUpdateBehavior.Update,
            TentacleUpdateAccountId = 42
        };

        var entity = new MachinePolicy
        {
            Id = 1, SpaceId = 1, Name = "test",
            MachineUpdatePolicy = JsonSerializer.Serialize(updateDto, JsonOptions)
        };

        var dto = MachinePolicyService.ToDto(entity);

        dto.MachineUpdatePolicy.CalamariUpdateBehavior.ShouldBe(CalamariUpdateBehavior.UpdateAlways);
        dto.MachineUpdatePolicy.TentacleUpdateBehavior.ShouldBe(AgentUpdateBehavior.Update);
        dto.MachineUpdatePolicy.KubernetesAgentUpdateBehavior.ShouldBe(AgentUpdateBehavior.Update);
        dto.MachineUpdatePolicy.TentacleUpdateAccountId.ShouldBe(42);
    }

    // ========================================================================
    // JSON enum serialization
    // ========================================================================

    [Fact]
    public void Serialize_Deserialize_EnumsAsStrings()
    {
        var dto = new MachineHealthCheckPolicyDto
        {
            HealthCheckScheduleType = HealthCheckScheduleType.Cron,
            HealthCheckType = PolicyHealthCheckType.OnlyConnectivity,
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                ["Bash"] = new() { RunType = ScriptPolicyRunType.CustomScript }
            }
        };

        var json = MachinePolicyService.Serialize(dto);

        json.ShouldContain("\"Cron\"");
        json.ShouldContain("\"OnlyConnectivity\"");
        json.ShouldContain("\"CustomScript\"");
        json.ShouldNotContain("\"2\""); // enum int value for Cron
        json.ShouldNotContain("\"1\""); // enum int value for OnlyConnectivity

        var roundTripped = MachinePolicyService.Deserialize<MachineHealthCheckPolicyDto>(json);

        roundTripped.HealthCheckScheduleType.ShouldBe(HealthCheckScheduleType.Cron);
        roundTripped.HealthCheckType.ShouldBe(PolicyHealthCheckType.OnlyConnectivity);
        roundTripped.ScriptPolicies["Bash"].RunType.ShouldBe(ScriptPolicyRunType.CustomScript);
    }

    // ========================================================================
    // JSON backward compatibility — old DB format
    // ========================================================================

    [Fact]
    public void Deserialize_OldJsonWithStringEnums_RoundTripsCorrectly()
    {
        var oldJson = """
        {
            "healthCheckScheduleType": "Interval",
            "healthCheckIntervalSeconds": 1800,
            "healthCheckType": "RunScript",
            "scriptPolicies": {
                "Bash": { "runType": "CustomScript", "scriptBody": "echo test" }
            }
        }
        """;

        var deserialized = MachinePolicyService.Deserialize<MachineHealthCheckPolicyDto>(oldJson);

        deserialized.HealthCheckScheduleType.ShouldBe(HealthCheckScheduleType.Interval);
        deserialized.HealthCheckType.ShouldBe(PolicyHealthCheckType.RunScript);
        deserialized.HealthCheckIntervalSeconds.ShouldBe(1800);
        deserialized.ScriptPolicies["Bash"].RunType.ShouldBe(ScriptPolicyRunType.CustomScript);
        deserialized.ScriptPolicies["Bash"].ScriptBody.ShouldBe("echo test");
    }

    [Fact]
    public void Deserialize_OldJsonWithoutNewFields_DefaultsCorrectly()
    {
        // Simulates old DB record that only had intervalSeconds and scriptPolicies
        var oldJson = """
        {
            "healthCheckIntervalSeconds": 600,
            "scriptPolicies": {}
        }
        """;

        var deserialized = MachinePolicyService.Deserialize<MachineHealthCheckPolicyDto>(oldJson);

        deserialized.HealthCheckScheduleType.ShouldBe(HealthCheckScheduleType.Interval);
        deserialized.HealthCheckType.ShouldBe(PolicyHealthCheckType.RunScript);
        deserialized.HealthCheckIntervalSeconds.ShouldBe(600);
        deserialized.HealthCheckCronExpression.ShouldBeNull();
    }

    // ========================================================================
    // DTO default values
    // ========================================================================

    [Fact]
    public void MachineHealthCheckPolicyDto_DefaultValues()
    {
        var dto = new MachineHealthCheckPolicyDto();

        dto.HealthCheckScheduleType.ShouldBe(HealthCheckScheduleType.Interval);
        dto.HealthCheckIntervalSeconds.ShouldBe(3600);
        dto.HealthCheckType.ShouldBe(PolicyHealthCheckType.RunScript);
        dto.HealthCheckCronExpression.ShouldBeNull();
        dto.ScriptPolicies.ShouldNotBeNull();
        dto.ScriptPolicies.ShouldBeEmpty();
    }

    [Fact]
    public void MachineConnectivityPolicyDto_DefaultValues()
    {
        var dto = new MachineConnectivityPolicyDto();

        dto.MachineConnectivityBehavior.ShouldBe(MachineConnectivityBehavior.ExpectedToBeOnline);
        dto.ConnectTimeoutSeconds.ShouldBe(60);
        dto.RetryAttempts.ShouldBe(5);
        dto.RetryWaitIntervalSeconds.ShouldBe(1);
        dto.RetryTimeLimitSeconds.ShouldBe(300);
        dto.PollingRequestQueueTimeoutSeconds.ShouldBe(600);
    }

    [Fact]
    public void MachineRpcCallRetryPolicyDto_DefaultValues()
    {
        var dto = new MachineRpcCallRetryPolicyDto();

        dto.Enabled.ShouldBeTrue();
        dto.DeploymentRetryDurationSeconds.ShouldBe(150);
        dto.HealthCheckRetryDurationSeconds.ShouldBe(150);
    }

    // ========================================================================
    // ApplyDto — full field round-trip
    // ========================================================================

    [Fact]
    public void ApplyDto_AllPolicies_SerializedCorrectly()
    {
        var dto = new MachinePolicyDto
        {
            SpaceId = 1, Name = "full-test",
            MachineHealthCheckPolicy = new MachineHealthCheckPolicyDto { HealthCheckIntervalSeconds = 600 },
            MachineConnectivityPolicy = new MachineConnectivityPolicyDto { ConnectTimeoutSeconds = 45 },
            MachineCleanupPolicy = new MachineCleanupPolicyDto { DeleteMachinesBehavior = DeleteMachinesBehavior.DeleteUnavailableMachines },
            MachineUpdatePolicy = new MachineUpdatePolicyDto { CalamariUpdateBehavior = CalamariUpdateBehavior.UpdateAlways },
            MachineRpcCallRetryPolicy = new MachineRpcCallRetryPolicyDto { Enabled = false }
        };

        var entity = new MachinePolicy();
        MachinePolicyService.ApplyDto(entity, dto);

        entity.MachineHealthCheckPolicy.ShouldNotBeNullOrWhiteSpace();
        entity.MachineConnectivityPolicy.ShouldNotBeNullOrWhiteSpace();
        entity.MachineCleanupPolicy.ShouldNotBeNullOrWhiteSpace();
        entity.MachineUpdatePolicy.ShouldNotBeNullOrWhiteSpace();
        entity.MachineRpcCallRetryPolicy.ShouldNotBeNullOrWhiteSpace();

        // Round-trip verification
        var roundTripped = MachinePolicyService.ToDto(entity);

        roundTripped.MachineHealthCheckPolicy.HealthCheckIntervalSeconds.ShouldBe(600);
        roundTripped.MachineConnectivityPolicy.ConnectTimeoutSeconds.ShouldBe(45);
        roundTripped.MachineCleanupPolicy.DeleteMachinesBehavior.ShouldBe(DeleteMachinesBehavior.DeleteUnavailableMachines);
        roundTripped.MachineUpdatePolicy.CalamariUpdateBehavior.ShouldBe(CalamariUpdateBehavior.UpdateAlways);
        roundTripped.MachineRpcCallRetryPolicy.Enabled.ShouldBeFalse();
    }

    // ========================================================================
    // DefaultMachinePolicySeeder — BuildDefaultPolicy
    // ========================================================================

    [Fact]
    public void BuildDefaultPolicy_IsDefault_True()
    {
        var policy = DefaultMachinePolicySeeder.BuildDefaultPolicy();

        policy.IsDefault.ShouldBeTrue();
        policy.Name.ShouldBe("Default Machine Policy");
        policy.SpaceId.ShouldBe(1);
    }

    [Fact]
    public void BuildDefaultPolicy_AllJsonFieldsPopulated()
    {
        var policy = DefaultMachinePolicySeeder.BuildDefaultPolicy();

        policy.MachineHealthCheckPolicy.ShouldNotBeNullOrWhiteSpace();
        policy.MachineConnectivityPolicy.ShouldNotBeNullOrWhiteSpace();
        policy.MachineCleanupPolicy.ShouldNotBeNullOrWhiteSpace();
        policy.MachineUpdatePolicy.ShouldNotBeNullOrWhiteSpace();
        policy.MachineRpcCallRetryPolicy.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void BuildDefaultPolicy_RoundTripsToDefaultDtoValues()
    {
        var policy = DefaultMachinePolicySeeder.BuildDefaultPolicy();
        var dto = MachinePolicyService.ToDto(policy);

        dto.MachineHealthCheckPolicy.HealthCheckScheduleType.ShouldBe(HealthCheckScheduleType.Interval);
        dto.MachineHealthCheckPolicy.HealthCheckIntervalSeconds.ShouldBe(3600);
        dto.MachineConnectivityPolicy.MachineConnectivityBehavior.ShouldBe(MachineConnectivityBehavior.ExpectedToBeOnline);
        dto.MachineCleanupPolicy.DeleteMachinesBehavior.ShouldBe(DeleteMachinesBehavior.DoNotDelete);
        dto.MachineUpdatePolicy.CalamariUpdateBehavior.ShouldBe(CalamariUpdateBehavior.UpdateOnDeployment);
        dto.MachineRpcCallRetryPolicy.Enabled.ShouldBeTrue();
    }

    // ========================================================================
    // DeleteAsync — reassign machines to default policy
    // ========================================================================

    [Fact]
    public async Task DeletePolicy_ReassignsMachinesToDefaultPolicy()
    {
        var policyDataProvider = new Mock<IMachinePolicyDataProvider>();
        var machineDataProvider = new Mock<IMachineDataProvider>();

        var customPolicy = new MachinePolicy { Id = 2, Name = "Custom", IsDefault = false };
        var defaultPolicy = new MachinePolicy { Id = 1, Name = "Default", IsDefault = true };

        var machines = new List<Machine>
        {
            new() { Id = 10, Name = "m1", MachinePolicyId = 2 },
            new() { Id = 11, Name = "m2", MachinePolicyId = 2 }
        };

        policyDataProvider.Setup(p => p.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(customPolicy);
        policyDataProvider.Setup(p => p.GetDefaultAsync(It.IsAny<CancellationToken>())).ReturnsAsync(defaultPolicy);
        machineDataProvider.Setup(p => p.GetMachinesByPolicyIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(machines);

        var service = new MachinePolicyService(policyDataProvider.Object, machineDataProvider.Object);

        await service.DeleteAsync(new DeleteMachinePolicyCommand { Id = 2 });

        machines[0].MachinePolicyId.ShouldBe(1);
        machines[1].MachinePolicyId.ShouldBe(1);
        machineDataProvider.Verify(p => p.UpdateMachineAsync(It.IsAny<Machine>(), true, It.IsAny<CancellationToken>()), Times.Exactly(2));
        policyDataProvider.Verify(p => p.DeleteAsync(customPolicy, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeletePolicy_NoAffectedMachines_StillDeletes()
    {
        var policyDataProvider = new Mock<IMachinePolicyDataProvider>();
        var machineDataProvider = new Mock<IMachineDataProvider>();

        var customPolicy = new MachinePolicy { Id = 2, Name = "Custom", IsDefault = false };

        policyDataProvider.Setup(p => p.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(customPolicy);
        machineDataProvider.Setup(p => p.GetMachinesByPolicyIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Machine>());

        var service = new MachinePolicyService(policyDataProvider.Object, machineDataProvider.Object);

        await service.DeleteAsync(new DeleteMachinePolicyCommand { Id = 2 });

        policyDataProvider.Verify(p => p.DeleteAsync(customPolicy, It.IsAny<CancellationToken>()), Times.Once);
        machineDataProvider.Verify(p => p.UpdateMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeletePolicy_DefaultPolicy_Throws()
    {
        var policyDataProvider = new Mock<IMachinePolicyDataProvider>();
        var machineDataProvider = new Mock<IMachineDataProvider>();

        var defaultPolicy = new MachinePolicy { Id = 1, Name = "Default", IsDefault = true };

        policyDataProvider.Setup(p => p.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(defaultPolicy);

        var service = new MachinePolicyService(policyDataProvider.Object, machineDataProvider.Object);

        await Should.ThrowAsync<InvalidOperationException>(() => service.DeleteAsync(new DeleteMachinePolicyCommand { Id = 1 }));

        policyDataProvider.Verify(p => p.DeleteAsync(It.IsAny<MachinePolicy>(), It.IsAny<CancellationToken>()), Times.Never);
        machineDataProvider.Verify(p => p.GetMachinesByPolicyIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Variables;

public class VariableScopeEvaluatorTests
{
    // ========== Applicability: Unscoped ==========

    [Fact]
    public void Evaluate_UnscopedVariable_AlwaysIncluded()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("DbHost", "localhost")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            EnvironmentId = 1, MachineId = 10
        });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("localhost");
    }

    [Fact]
    public void Evaluate_UnscopedVariable_IncludedWhenContextIsEmpty()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("Key", "val")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext());

        result.Count.ShouldBe(1);
    }

    // ========== Applicability: Environment Scope ==========

    [Fact]
    public void Evaluate_EnvironmentScoped_MatchingEnv_Included()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("DbHost", "prod-db", envScope: "1")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            EnvironmentId = 1
        });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("prod-db");
    }

    [Fact]
    public void Evaluate_EnvironmentScoped_NonMatchingEnv_Excluded()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("DbHost", "prod-db", envScope: "1")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            EnvironmentId = 2
        });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_EnvironmentScoped_NoEnvInContext_Excluded()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("DbHost", "prod-db", envScope: "1")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext());

        result.ShouldBeEmpty();
    }

    // ========== Applicability: Machine Scope ==========

    [Fact]
    public void Evaluate_MachineScoped_MatchingMachine_Included()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("Port", "8080", machineScope: "10")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            MachineId = 10
        });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("8080");
    }

    [Fact]
    public void Evaluate_MachineScoped_NonMatchingMachine_Excluded()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("Port", "8080", machineScope: "10")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            MachineId = 99
        });

        result.ShouldBeEmpty();
    }

    // ========== Applicability: AND across scope types ==========

    [Fact]
    public void Evaluate_EnvAndMachineScoped_BothMatch_Included()
    {
        var variable = MakeVariable("Config", "specific");
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Machine, "10"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { EnvironmentId = 1, MachineId = 10 });

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Evaluate_EnvAndMachineScoped_OnlyEnvMatches_Excluded()
    {
        var variable = MakeVariable("Config", "specific");
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Machine, "10"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { EnvironmentId = 1, MachineId = 99 });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_EnvAndMachineScoped_OnlyMachineMatches_Excluded()
    {
        var variable = MakeVariable("Config", "specific");
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Machine, "10"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { EnvironmentId = 2, MachineId = 10 });

        result.ShouldBeEmpty();
    }

    // ========== Applicability: OR within scope type ==========

    [Fact]
    public void Evaluate_MultipleEnvValues_AnyMatch_Included()
    {
        var variable = MakeVariable("DbHost", "shared-db");
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "2"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { EnvironmentId = 2 });

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Evaluate_MultipleEnvValues_NoneMatch_Excluded()
    {
        var variable = MakeVariable("DbHost", "shared-db");
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "2"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { EnvironmentId = 99 });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_MultipleMachineValues_AnyMatch_Included()
    {
        var variable = MakeVariable("Port", "9090");
        variable.Scopes.Add(MakeScope(VariableScopeType.Machine, "10"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Machine, "20"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { MachineId = 20 });

        result.Count.ShouldBe(1);
    }

    // ========== Precedence: Same Name, Different Scopes ==========

    [Fact]
    public void Evaluate_SameName_MachineScopedBeatEnvironmentScoped()
    {
        var envScoped = MakeVariable("DbHost", "env-db", envScope: "1");
        var machineScoped = MakeVariable("DbHost", "machine-db", machineScope: "10");

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { envScoped, machineScoped },
            new VariableScopeContext { EnvironmentId = 1, MachineId = 10 });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("machine-db");
    }

    [Fact]
    public void Evaluate_SameName_EnvironmentScopedBeatsUnscoped()
    {
        var unscoped = MakeVariable("DbHost", "default-db");
        var envScoped = MakeVariable("DbHost", "prod-db", envScope: "1");

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { unscoped, envScoped },
            new VariableScopeContext { EnvironmentId = 1 });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("prod-db");
    }

    [Fact]
    public void Evaluate_SameName_EnvPlusMachineBeatsEitherAlone()
    {
        var envOnly = MakeVariable("Config", "env-only", envScope: "1");
        var machineOnly = MakeVariable("Config", "machine-only", machineScope: "10");
        var both = MakeVariable("Config", "both");
        both.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));
        both.Scopes.Add(MakeScope(VariableScopeType.Machine, "10"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { envOnly, machineOnly, both },
            new VariableScopeContext { EnvironmentId = 1, MachineId = 10 });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("both");
    }

    [Fact]
    public void Evaluate_SameName_UnscopedWins_WhenScopedDoesNotMatch()
    {
        var unscoped = MakeVariable("DbHost", "default-db");
        var envScoped = MakeVariable("DbHost", "prod-db", envScope: "1");

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { unscoped, envScoped },
            new VariableScopeContext { EnvironmentId = 2 });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("default-db");
    }

    [Fact]
    public void Evaluate_SameName_ThreeTiers_MostSpecificWins()
    {
        var unscoped = MakeVariable("Msg", "default");
        var envScoped = MakeVariable("Msg", "env-specific", envScope: "1");
        var machineScoped = MakeVariable("Msg", "machine-specific", machineScope: "10");

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { unscoped, envScoped, machineScoped },
            new VariableScopeContext { EnvironmentId = 1, MachineId = 10 });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("machine-specific");
    }

    // ========== Name Case-Insensitive Grouping ==========

    [Fact]
    public void Evaluate_CaseInsensitiveNameGrouping_MostSpecificWins()
    {
        var lower = MakeVariable("dbhost", "lower-val");
        var upper = MakeVariable("DBHOST", "upper-val", envScope: "1");

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { lower, upper },
            new VariableScopeContext { EnvironmentId = 1 });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("upper-val");
    }

    // ========== Edge Cases ==========

    [Fact]
    public void Evaluate_EmptyList_ReturnsEmpty()
    {
        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto>(), new VariableScopeContext { EnvironmentId = 1 });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_NullList_ReturnsEmpty()
    {
        var result = VariableScopeEvaluator.Evaluate(
            null, new VariableScopeContext { EnvironmentId = 1 });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_AllFiltered_ReturnsEmpty()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("A", "1", envScope: "1"),
            MakeVariable("B", "2", machineScope: "10")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            EnvironmentId = 99, MachineId = 99
        });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_MixedScopedAndUnscoped_DifferentNames_AllIncluded()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("Global", "g"),
            MakeVariable("EnvSpecific", "e", envScope: "1"),
            MakeVariable("MachineSpecific", "m", machineScope: "10")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            EnvironmentId = 1, MachineId = 10
        });

        result.Count.ShouldBe(3);
    }

    [Fact]
    public void Evaluate_VariableWithNullScopes_TreatedAsUnscoped()
    {
        var variable = new VariableDto { Name = "Key", Value = "val", Scopes = null };

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable }, new VariableScopeContext { EnvironmentId = 1 });

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Evaluate_VariableWithEmptyScopes_TreatedAsUnscoped()
    {
        var variable = new VariableDto { Name = "Key", Value = "val", Scopes = new() };

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable }, new VariableScopeContext { EnvironmentId = 1 });

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Evaluate_PreservesVariableProperties()
    {
        var variable = MakeVariable("Secret", "s3cret", envScope: "1");
        variable.IsSensitive = true;
        variable.Description = "A secret";

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { EnvironmentId = 1 });

        result.Count.ShouldBe(1);
        result[0].IsSensitive.ShouldBeTrue();
        result[0].Description.ShouldBe("A secret");
    }

    // ========== Rank Computation ==========

    [Fact]
    public void ComputeRank_Unscoped_ReturnsZero()
    {
        var variable = MakeVariable("X", "v");

        VariableScopeEvaluator.ComputeRank(variable).ShouldBe(0);
    }

    [Fact]
    public void ComputeRank_EnvironmentOnly_Returns100()
    {
        var variable = MakeVariable("X", "v", envScope: "1");

        VariableScopeEvaluator.ComputeRank(variable).ShouldBe(100);
    }

    [Fact]
    public void ComputeRank_MachineOnly_Returns1000000()
    {
        var variable = MakeVariable("X", "v", machineScope: "10");

        VariableScopeEvaluator.ComputeRank(variable).ShouldBe(1_000_000);
    }

    [Fact]
    public void ComputeRank_EnvAndMachine_ReturnsCombined()
    {
        var variable = MakeVariable("X", "v");
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Machine, "10"));

        VariableScopeEvaluator.ComputeRank(variable).ShouldBe(1_000_100);
    }

    [Fact]
    public void ComputeRank_MultipleEnvValues_CountsOnce()
    {
        var variable = MakeVariable("X", "v");
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "2"));

        VariableScopeEvaluator.ComputeRank(variable).ShouldBe(100);
    }

    // ========== Real-World Scenarios ==========

    [Theory]
    [InlineData(1, "staging-db")]
    [InlineData(2, "prod-db")]
    [InlineData(99, "default-db")]
    public void Evaluate_MultiEnvironmentDeployment_CorrectValuePerEnv(int envId, string expected)
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("DbHost", "default-db"),
            MakeVariable("DbHost", "staging-db", envScope: "1"),
            MakeVariable("DbHost", "prod-db", envScope: "2")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            EnvironmentId = envId
        });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe(expected);
    }

    [Fact]
    public void Evaluate_MultiTarget_EachMachineGetsOwnValue()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("Port", "8080"),
            MakeVariable("Port", "9090", machineScope: "10"),
            MakeVariable("Port", "7070", machineScope: "20")
        };

        var resultM10 = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext { MachineId = 10 });
        var resultM20 = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext { MachineId = 20 });
        var resultM99 = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext { MachineId = 99 });

        resultM10.Single().Value.ShouldBe("9090");
        resultM20.Single().Value.ShouldBe("7070");
        resultM99.Single().Value.ShouldBe("8080");
    }

    [Fact]
    public void Evaluate_ComplexScenario_EnvAndMachineOverrideLayers()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("Config", "default"),
            MakeVariable("Config", "prod-default", envScope: "2"),
            MakeVariable("Config", "prod-machine10"),
            MakeVariable("SharedKey", "shared-val")
        };

        variables[2].Scopes.Add(MakeScope(VariableScopeType.Environment, "2"));
        variables[2].Scopes.Add(MakeScope(VariableScopeType.Machine, "10"));

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            EnvironmentId = 2, MachineId = 10
        });

        result.Count.ShouldBe(2);
        result.Single(v => v.Name == "Config").Value.ShouldBe("prod-machine10");
        result.Single(v => v.Name == "SharedKey").Value.ShouldBe("shared-val");
    }

    // ========== Applicability: Role Scope ==========

    [Fact]
    public void Evaluate_RoleScoped_MatchingRole_Included()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("Config", "web-config", roleScope: "web-server")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            Roles = Roles("web-server", "api-server")
        });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("web-config");
    }

    [Fact]
    public void Evaluate_RoleScoped_NonMatchingRole_Excluded()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("Config", "web-config", roleScope: "web-server")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            Roles = Roles("database-server")
        });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_RoleScoped_NoRolesInContext_Excluded()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("Config", "web-config", roleScope: "web-server")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext());

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_RoleScoped_NullRolesInContext_Excluded()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("Config", "web-config", roleScope: "web-server")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            Roles = null
        });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_RoleScoped_CaseInsensitiveMatch()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("Config", "val", roleScope: "Web-Server")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            Roles = Roles("web-server")
        });

        result.Count.ShouldBe(1);
    }

    // ========== Applicability: Role OR within type ==========

    [Fact]
    public void Evaluate_MultipleRoleValues_AnyMatch_Included()
    {
        var variable = MakeVariable("Config", "shared");
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "api-server"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { Roles = Roles("api-server") });

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Evaluate_MultipleRoleValues_NoneMatch_Excluded()
    {
        var variable = MakeVariable("Config", "shared");
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "api-server"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { Roles = Roles("database-server") });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_MultipleRoleValues_TargetHasMultipleRoles_OneOverlaps_Included()
    {
        var variable = MakeVariable("Config", "frontend");
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "cdn-server"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { Roles = Roles("web-server", "load-balancer") });

        result.Count.ShouldBe(1);
    }

    // ========== Applicability: Channel Scope ==========

    [Fact]
    public void Evaluate_ChannelScoped_MatchingChannel_Included()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("Feature", "enabled", channelScope: "5")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            ChannelId = 5
        });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("enabled");
    }

    [Fact]
    public void Evaluate_ChannelScoped_NonMatchingChannel_Excluded()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("Feature", "enabled", channelScope: "5")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            ChannelId = 99
        });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_ChannelScoped_NoChannelInContext_Excluded()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("Feature", "enabled", channelScope: "5")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext());

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_MultipleChannelValues_AnyMatch_Included()
    {
        var variable = MakeVariable("Feature", "enabled");
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "5"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "6"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { ChannelId = 6 });

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Evaluate_MultipleChannelValues_NoneMatch_Excluded()
    {
        var variable = MakeVariable("Feature", "enabled");
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "5"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "6"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { ChannelId = 99 });

        result.ShouldBeEmpty();
    }

    // ========== AND across scope types: Role combinations ==========

    [Fact]
    public void Evaluate_RoleAndEnv_BothMatch_Included()
    {
        var variable = MakeVariable("Config", "role-env");
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { EnvironmentId = 1, Roles = Roles("web-server") });

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Evaluate_RoleAndEnv_OnlyRoleMatches_Excluded()
    {
        var variable = MakeVariable("Config", "role-env");
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { EnvironmentId = 99, Roles = Roles("web-server") });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_RoleAndEnv_OnlyEnvMatches_Excluded()
    {
        var variable = MakeVariable("Config", "role-env");
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { EnvironmentId = 1, Roles = Roles("database-server") });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_RoleAndMachine_BothMatch_Included()
    {
        var variable = MakeVariable("Config", "role-machine");
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Machine, "10"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { MachineId = 10, Roles = Roles("web-server") });

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Evaluate_RoleAndMachine_OnlyMachineMatches_Excluded()
    {
        var variable = MakeVariable("Config", "role-machine");
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Machine, "10"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { MachineId = 10, Roles = Roles("database-server") });

        result.ShouldBeEmpty();
    }

    // ========== AND across scope types: Channel combinations ==========

    [Fact]
    public void Evaluate_ChannelAndEnv_BothMatch_Included()
    {
        var variable = MakeVariable("Config", "ch-env");
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "5"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { EnvironmentId = 1, ChannelId = 5 });

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Evaluate_ChannelAndEnv_OnlyChannelMatches_Excluded()
    {
        var variable = MakeVariable("Config", "ch-env");
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "5"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { EnvironmentId = 99, ChannelId = 5 });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_ChannelAndMachine_BothMatch_Included()
    {
        var variable = MakeVariable("Config", "ch-machine");
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "5"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Machine, "10"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { MachineId = 10, ChannelId = 5 });

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Evaluate_RoleAndChannel_BothMatch_Included()
    {
        var variable = MakeVariable("Config", "role-ch");
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "5"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { Roles = Roles("web-server"), ChannelId = 5 });

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Evaluate_RoleAndChannel_OnlyRoleMatches_Excluded()
    {
        var variable = MakeVariable("Config", "role-ch");
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "5"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { Roles = Roles("web-server"), ChannelId = 99 });

        result.ShouldBeEmpty();
    }

    // ========== AND: All four scope types ==========

    [Fact]
    public void Evaluate_AllFourScopeTypes_AllMatch_Included()
    {
        var variable = MakeVariable("Config", "ultra-specific");
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Machine, "10"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "5"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext
            {
                EnvironmentId = 1, MachineId = 10,
                Roles = Roles("web-server"), ChannelId = 5
            });

        result.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(99, 10, true, 5)]    // env mismatch
    [InlineData(1, 99, true, 5)]     // machine mismatch
    [InlineData(1, 10, false, 5)]    // role mismatch
    [InlineData(1, 10, true, 99)]    // channel mismatch
    public void Evaluate_AllFourScopeTypes_OneMismatch_Excluded(
        int envId, int machineId, bool roleMatches, int channelId)
    {
        var variable = MakeVariable("Config", "ultra-specific");
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Machine, "10"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "5"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext
            {
                EnvironmentId = envId, MachineId = machineId,
                Roles = Roles(roleMatches ? "web-server" : "database-server"),
                ChannelId = channelId
            });

        result.ShouldBeEmpty();
    }

    // ========== AND + OR combination: multi-select within each type ==========

    [Fact]
    public void Evaluate_MultiRoleAndMultiEnv_CrossProduct_Included()
    {
        var variable = MakeVariable("Config", "multi");
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "api-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "2"));

        // env=2 matches (OR), role=api-server matches (OR) → AND satisfied
        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { EnvironmentId = 2, Roles = Roles("api-server") });

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Evaluate_MultiRoleAndMultiEnv_EnvMismatch_Excluded()
    {
        var variable = MakeVariable("Config", "multi");
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "api-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "2"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { EnvironmentId = 99, Roles = Roles("web-server") });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_MultiRoleMultiChannelMultiEnv_AllMatchViaOR_Included()
    {
        var variable = MakeVariable("Config", "complex");
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "api-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "5"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "6"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "2"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext
            {
                EnvironmentId = 2, Roles = Roles("api-server"), ChannelId = 6
            });

        result.Count.ShouldBe(1);
    }

    // ========== Precedence: Role scope ==========

    [Fact]
    public void Evaluate_SameName_RoleScopedBeatsEnvScoped()
    {
        var envScoped = MakeVariable("Config", "env-val", envScope: "1");
        var roleScoped = MakeVariable("Config", "role-val", roleScope: "web-server");

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { envScoped, roleScoped },
            new VariableScopeContext { EnvironmentId = 1, Roles = Roles("web-server") });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("role-val");
    }

    [Fact]
    public void Evaluate_SameName_RoleScopedBeatsChannelScoped()
    {
        var channelScoped = MakeVariable("Config", "ch-val", channelScope: "5");
        var roleScoped = MakeVariable("Config", "role-val", roleScope: "web-server");

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { channelScoped, roleScoped },
            new VariableScopeContext { Roles = Roles("web-server"), ChannelId = 5 });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("role-val");
    }

    [Fact]
    public void Evaluate_SameName_MachineScopedBeatsRoleScoped()
    {
        var roleScoped = MakeVariable("Config", "role-val", roleScope: "web-server");
        var machineScoped = MakeVariable("Config", "machine-val", machineScope: "10");

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { roleScoped, machineScoped },
            new VariableScopeContext { MachineId = 10, Roles = Roles("web-server") });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("machine-val");
    }

    [Fact]
    public void Evaluate_SameName_ChannelScopedBeatsUnscoped()
    {
        var unscoped = MakeVariable("Config", "default");
        var channelScoped = MakeVariable("Config", "channel-val", channelScope: "5");

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { unscoped, channelScoped },
            new VariableScopeContext { ChannelId = 5 });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("channel-val");
    }

    [Fact]
    public void Evaluate_SameName_EnvScopedBeatsChannelScoped()
    {
        var channelScoped = MakeVariable("Config", "ch-val", channelScope: "5");
        var envScoped = MakeVariable("Config", "env-val", envScope: "1");

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { channelScoped, envScoped },
            new VariableScopeContext { EnvironmentId = 1, ChannelId = 5 });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("env-val");
    }

    [Fact]
    public void Evaluate_SameName_FiveTiers_MostSpecificWins()
    {
        var unscoped = MakeVariable("Msg", "default");
        var channelScoped = MakeVariable("Msg", "channel", channelScope: "5");
        var envScoped = MakeVariable("Msg", "env", envScope: "1");
        var roleScoped = MakeVariable("Msg", "role", roleScope: "web-server");
        var machineScoped = MakeVariable("Msg", "machine", machineScope: "10");

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { unscoped, channelScoped, envScoped, roleScoped, machineScoped },
            new VariableScopeContext
            {
                EnvironmentId = 1, MachineId = 10,
                Roles = Roles("web-server"), ChannelId = 5
            });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("machine");
    }

    [Fact]
    public void Evaluate_SameName_RolePlusEnvBeatsMachineAlone()
    {
        // Role(10000) + Env(100) = 10100 > Machine(1000000)? No — Machine is 1M.
        // Actually Machine alone = 1,000,000 > Role+Env = 10,100
        // So machine still wins. Let's test Role+Env vs Env alone.
        var envOnly = MakeVariable("Config", "env-only", envScope: "1");
        var roleAndEnv = MakeVariable("Config", "role-env");
        roleAndEnv.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        roleAndEnv.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { envOnly, roleAndEnv },
            new VariableScopeContext { EnvironmentId = 1, Roles = Roles("web-server") });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("role-env");
    }

    [Fact]
    public void Evaluate_SameName_AllFourScopesBeatsMachineAlone()
    {
        var machineOnly = MakeVariable("Config", "machine-only", machineScope: "10");
        var allFour = MakeVariable("Config", "all-four");
        allFour.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));
        allFour.Scopes.Add(MakeScope(VariableScopeType.Machine, "10"));
        allFour.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        allFour.Scopes.Add(MakeScope(VariableScopeType.Channel, "5"));

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { machineOnly, allFour },
            new VariableScopeContext
            {
                EnvironmentId = 1, MachineId = 10,
                Roles = Roles("web-server"), ChannelId = 5
            });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("all-four");
    }

    // ========== Rank Computation: Role and Channel ==========

    [Fact]
    public void ComputeRank_RoleOnly_Returns10000()
    {
        var variable = MakeVariable("X", "v", roleScope: "web-server");

        VariableScopeEvaluator.ComputeRank(variable).ShouldBe(10_000);
    }

    [Fact]
    public void ComputeRank_ChannelOnly_Returns10()
    {
        var variable = MakeVariable("X", "v", channelScope: "5");

        VariableScopeEvaluator.ComputeRank(variable).ShouldBe(10);
    }

    [Fact]
    public void ComputeRank_RoleAndEnv_ReturnsCombined()
    {
        var variable = MakeVariable("X", "v");
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));

        VariableScopeEvaluator.ComputeRank(variable).ShouldBe(10_100);
    }

    [Fact]
    public void ComputeRank_ChannelAndEnv_ReturnsCombined()
    {
        var variable = MakeVariable("X", "v");
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "5"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));

        VariableScopeEvaluator.ComputeRank(variable).ShouldBe(110);
    }

    [Fact]
    public void ComputeRank_AllFourTypes_ReturnsCombined()
    {
        var variable = MakeVariable("X", "v");
        variable.Scopes.Add(MakeScope(VariableScopeType.Environment, "1"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Machine, "10"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "5"));

        VariableScopeEvaluator.ComputeRank(variable).ShouldBe(1_010_110);
    }

    [Fact]
    public void ComputeRank_MultipleRoleValues_CountsOnce()
    {
        var variable = MakeVariable("X", "v");
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "web-server"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Role, "api-server"));

        VariableScopeEvaluator.ComputeRank(variable).ShouldBe(10_000);
    }

    [Fact]
    public void ComputeRank_MultipleChannelValues_CountsOnce()
    {
        var variable = MakeVariable("X", "v");
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "5"));
        variable.Scopes.Add(MakeScope(VariableScopeType.Channel, "6"));

        VariableScopeEvaluator.ComputeRank(variable).ShouldBe(10);
    }

    // ========== Precedence ordering guarantee ==========

    [Theory]
    [InlineData(0, 10, 100, 10_000, 1_000_000)]
    public void ComputeRank_OrderingIsCorrect(
        int unscopedRank, int channelRank, int envRank, int roleRank, int machineRank)
    {
        VariableScopeEvaluator.ComputeRank(MakeVariable("X", "v")).ShouldBe(unscopedRank);
        VariableScopeEvaluator.ComputeRank(MakeVariable("X", "v", channelScope: "1")).ShouldBe(channelRank);
        VariableScopeEvaluator.ComputeRank(MakeVariable("X", "v", envScope: "1")).ShouldBe(envRank);
        VariableScopeEvaluator.ComputeRank(MakeVariable("X", "v", roleScope: "r")).ShouldBe(roleRank);
        VariableScopeEvaluator.ComputeRank(MakeVariable("X", "v", machineScope: "1")).ShouldBe(machineRank);

        (unscopedRank < channelRank).ShouldBeTrue();
        (channelRank < envRank).ShouldBeTrue();
        (envRank < roleRank).ShouldBeTrue();
        (roleRank < machineRank).ShouldBeTrue();
    }

    // ========== Real-World Scenarios with Role/Channel ==========

    [Theory]
    [InlineData("web-server", "web-config")]
    [InlineData("api-server", "api-config")]
    [InlineData("database-server", "default-config")]
    public void Evaluate_DifferentRoles_GetDifferentValues(string role, string expected)
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("Config", "default-config"),
            MakeVariable("Config", "web-config", roleScope: "web-server"),
            MakeVariable("Config", "api-config", roleScope: "api-server")
        };

        var result = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            Roles = Roles(role)
        });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe(expected);
    }

    [Fact]
    public void Evaluate_FullPipeline_EnvRoleMachineChannel_LayeredOverrides()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("DbHost", "default-db"),                                       // rank 0
            MakeVariable("DbHost", "stable-db", channelScope: "1"),                     // rank 10
            MakeVariable("DbHost", "prod-db", envScope: "2"),                           // rank 100
            MakeVariable("DbHost", "web-db", roleScope: "web-server"),                  // rank 10000
            MakeVariable("DbHost", "machine5-db", machineScope: "5"),                   // rank 1000000
            MakeVariable("ApiKey", "default-key"),
            MakeVariable("ApiKey", "prod-key", envScope: "2"),
            MakeVariable("Timeout", "30"),
            MakeVariable("Timeout", "60", roleScope: "web-server"),
        };

        var ctx = new VariableScopeContext
        {
            EnvironmentId = 2, MachineId = 5,
            Roles = Roles("web-server"), ChannelId = 1
        };

        var result = VariableScopeEvaluator.Evaluate(variables, ctx);

        result.Count.ShouldBe(3);
        result.Single(v => v.Name == "DbHost").Value.ShouldBe("machine5-db");
        result.Single(v => v.Name == "ApiKey").Value.ShouldBe("prod-key");
        result.Single(v => v.Name == "Timeout").Value.ShouldBe("60");
    }

    [Fact]
    public void Evaluate_MultiTarget_RoleScopedVariable_PerRoleValues()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("Port", "8080"),
            MakeVariable("Port", "443", roleScope: "web-server"),
            MakeVariable("Port", "5432", roleScope: "database-server")
        };

        var webResult = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            Roles = Roles("web-server")
        });

        var dbResult = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            Roles = Roles("database-server")
        });

        var otherResult = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            Roles = Roles("cache-server")
        });

        webResult.Single().Value.ShouldBe("443");
        dbResult.Single().Value.ShouldBe("5432");
        otherResult.Single().Value.ShouldBe("8080");
    }

    [Fact]
    public void Evaluate_ChannelBasedFeatureFlag_StableVsPrerelease()
    {
        var variables = new List<VariableDto>
        {
            MakeVariable("FeatureX", "disabled"),
            MakeVariable("FeatureX", "enabled", channelScope: "2"),  // channel 2 = prerelease
        };

        var stableResult = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            ChannelId = 1  // stable channel
        });

        var prereleaseResult = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext
        {
            ChannelId = 2  // prerelease channel
        });

        stableResult.Single().Value.ShouldBe("disabled");
        prereleaseResult.Single().Value.ShouldBe("enabled");
    }

    [Fact]
    public void Evaluate_RoleScopedFallback_WhenMachineScopedDoesNotMatch()
    {
        var roleScoped = MakeVariable("Config", "role-val", roleScope: "web-server");
        var machineScoped = MakeVariable("Config", "machine-val", machineScope: "10");

        // Machine 99 doesn't match, so machine-scoped is filtered out.
        // Only role-scoped remains.
        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { roleScoped, machineScoped },
            new VariableScopeContext { MachineId = 99, Roles = Roles("web-server") });

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("role-val");
    }

    // ========== Name-Based Scope Matching ==========

    [Theory]
    [InlineData("TEST", "TEST", true)]
    [InlineData("test", "TEST", true)]
    [InlineData("TEST", "PRD", false)]
    public void IsApplicable_EnvironmentScopedByName_MatchesCaseInsensitively(string scopeValue, string envName, bool expected)
    {
        var variable = MakeVariable("Namespace", "my-ns", envScope: scopeValue);

        var result = VariableScopeEvaluator.IsApplicable(variable, new VariableScopeContext
        {
            EnvironmentId = 1,
            EnvironmentName = envName
        });

        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("MyMachine", "MyMachine", true)]
    [InlineData("mymachine", "MyMachine", true)]
    [InlineData("OtherMachine", "MyMachine", false)]
    public void IsApplicable_MachineScopedByName_MatchesCaseInsensitively(string scopeValue, string machineName, bool expected)
    {
        var variable = MakeVariable("Key", "val", machineScope: scopeValue);

        var result = VariableScopeEvaluator.IsApplicable(variable, new VariableScopeContext
        {
            MachineId = 10,
            MachineName = machineName
        });

        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Stable", "Stable", true)]
    [InlineData("stable", "Stable", true)]
    [InlineData("Beta", "Stable", false)]
    public void IsApplicable_ChannelScopedByName_MatchesCaseInsensitively(string scopeValue, string channelName, bool expected)
    {
        var variable = MakeVariable("Key", "val", channelScope: scopeValue);

        var result = VariableScopeEvaluator.IsApplicable(variable, new VariableScopeContext
        {
            ChannelId = 5,
            ChannelName = channelName
        });

        result.ShouldBe(expected);
    }

    [Fact]
    public void IsApplicable_ScopeMatchesByName_WhenIdDoesNotMatch()
    {
        // Scope value is a name "TEST", EnvironmentId is 99 (no match), but EnvironmentName is "TEST" (match)
        var variable = MakeVariable("Namespace", "test-ns", envScope: "TEST");

        var result = VariableScopeEvaluator.IsApplicable(variable, new VariableScopeContext
        {
            EnvironmentId = 99,
            EnvironmentName = "TEST"
        });

        result.ShouldBeTrue();
    }

    [Fact]
    public void IsApplicable_ScopeMatchesById_WhenNameDoesNotMatch()
    {
        // Scope value is "1" (ID match), EnvironmentName is "PRD" (no match against "1")
        var variable = MakeVariable("Namespace", "prod-ns", envScope: "1");

        var result = VariableScopeEvaluator.IsApplicable(variable, new VariableScopeContext
        {
            EnvironmentId = 1,
            EnvironmentName = "PRD"
        });

        result.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_NameBasedScoping_HigherRankWins()
    {
        // Unscoped fallback vs environment-scoped by name — scoped should win
        var unscoped = MakeVariable("Namespace", "default");
        var envScoped = MakeVariable("Namespace", "test-ns", envScope: "TEST");

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { unscoped, envScoped },
            new VariableScopeContext { EnvironmentId = 1, EnvironmentName = "TEST" });

        result.Single().Value.ShouldBe("test-ns");
    }

    [Fact]
    public void Evaluate_MixedIdAndNameScopes_BothMatch()
    {
        // Environment scoped by name + Machine scoped by ID — both must match (AND across types)
        var variable = MakeVariable("Config", "special-val", envScope: "TEST", machineScope: "10");

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { EnvironmentId = 99, EnvironmentName = "TEST", MachineId = 10, MachineName = "Worker1" });

        result.Single().Value.ShouldBe("special-val");
    }

    [Fact]
    public void Evaluate_MixedIdAndNameScopes_OneMismatches_Excluded()
    {
        // Environment matches by name, but Machine matches neither ID nor name
        var variable = MakeVariable("Config", "val", envScope: "TEST", machineScope: "Worker2");

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { variable },
            new VariableScopeContext { EnvironmentId = 99, EnvironmentName = "TEST", MachineId = 10, MachineName = "Worker1" });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_NameBasedPrecedence_MachineNameBeatsEnvironmentName()
    {
        var envScoped = MakeVariable("Key", "env-val", envScope: "TEST");
        var machineScoped = MakeVariable("Key", "machine-val", machineScope: "Worker1");

        var result = VariableScopeEvaluator.Evaluate(
            new List<VariableDto> { envScoped, machineScoped },
            new VariableScopeContext { EnvironmentId = 1, EnvironmentName = "TEST", MachineId = 10, MachineName = "Worker1" });

        result.Single().Value.ShouldBe("machine-val");
    }

    // ========== Helpers ==========

    private static VariableDto MakeVariable(string name, string value,
        string envScope = null, string machineScope = null,
        string roleScope = null, string channelScope = null)
    {
        var variable = new VariableDto
        {
            Name = name,
            Value = value,
            Scopes = new List<VariableScopeDto>()
        };

        if (envScope != null)
            variable.Scopes.Add(MakeScope(VariableScopeType.Environment, envScope));

        if (machineScope != null)
            variable.Scopes.Add(MakeScope(VariableScopeType.Machine, machineScope));

        if (roleScope != null)
            variable.Scopes.Add(MakeScope(VariableScopeType.Role, roleScope));

        if (channelScope != null)
            variable.Scopes.Add(MakeScope(VariableScopeType.Channel, channelScope));

        return variable;
    }

    private static HashSet<string> Roles(params string[] roles)
        => new(roles, StringComparer.OrdinalIgnoreCase);

    private static VariableScopeDto MakeScope(VariableScopeType type, string value)
    {
        return new VariableScopeDto { ScopeType = type, ScopeValue = value };
    }
}

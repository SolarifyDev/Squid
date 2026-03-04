using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.Deployments;

public class SensitiveOutputVariableTests
{
    // ========== ServiceMessageParser ==========

    [Fact]
    public void ParseOutputVariables_SensitiveTrue_FlagPreserved()
    {
        var lines = new List<string>
        {
            "##squid[setVariable name='Token' value='secret123' sensitive='True']"
        };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result.ShouldContainKey("Token");
        result["Token"].Value.ShouldBe("secret123");
        result["Token"].IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public void ParseOutputVariables_SensitiveFalse_FlagPreserved()
    {
        var lines = new List<string>
        {
            "##squid[setVariable name='Output' value='hello' sensitive='False']"
        };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result["Output"].IsSensitive.ShouldBeFalse();
    }

    [Fact]
    public void ParseOutputVariables_NoSensitiveAttribute_DefaultsFalse()
    {
        var lines = new List<string>
        {
            "##squid[setVariable name='Output' value='hello']"
        };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result["Output"].IsSensitive.ShouldBeFalse();
    }

    [Fact]
    public void ParseOutputVariables_SensitiveCaseInsensitive()
    {
        var lines = new List<string>
        {
            "##squid[setVariable name='Token' value='abc' sensitive='true']"
        };

        var result = ServiceMessageParser.ParseOutputVariables(lines);

        result["Token"].IsSensitive.ShouldBeTrue();
    }

    // ========== CaptureOutputVariables → SensitiveOutputVariableNames ==========

    [Fact]
    public void ActionExecutionResult_SensitiveOutputNames_TrackedSeparately()
    {
        var actionResult = new ActionExecutionResult
        {
            ExecutionMode = ExecutionMode.DirectScript
        };

        var lines = new List<string>
        {
            "##squid[setVariable name='PublicKey' value='pk123']",
            "##squid[setVariable name='PrivateKey' value='sk456' sensitive='True']",
            "##squid[setVariable name='Token' value='tok789' sensitive='True']"
        };

        SimulateCaptureOutputVariables(actionResult, lines);

        actionResult.OutputVariables.Count.ShouldBe(3);
        actionResult.SensitiveOutputVariableNames.Count.ShouldBe(2);
        actionResult.SensitiveOutputVariableNames.ShouldContain("PrivateKey");
        actionResult.SensitiveOutputVariableNames.ShouldContain("Token");
        actionResult.SensitiveOutputVariableNames.ShouldNotContain("PublicKey");
    }

    [Fact]
    public void ActionExecutionResult_SensitiveNames_CaseInsensitive()
    {
        var actionResult = new ActionExecutionResult
        {
            ExecutionMode = ExecutionMode.DirectScript
        };

        var lines = new List<string>
        {
            "##squid[setVariable name='secret' value='v1' sensitive='True']"
        };

        SimulateCaptureOutputVariables(actionResult, lines);

        actionResult.SensitiveOutputVariableNames.Contains("SECRET").ShouldBeTrue();
        actionResult.SensitiveOutputVariableNames.Contains("secret").ShouldBeTrue();
    }

    // ========== CollectOutputVariables → IsSensitive on VariableDto ==========

    [Fact]
    public void CollectOutputVariables_SensitiveFlag_PropagatedToDto()
    {
        var actionResult = new ActionExecutionResult
        {
            ExecutionMode = ExecutionMode.DirectScript,
            OutputVariables = new Dictionary<string, string>
            {
                ["PublicResult"] = "open",
                ["SecretResult"] = "hidden"
            },
            SensitiveOutputVariableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SecretResult"
            }
        };

        var collected = SimulateCollectOutputVariables("Step1", actionResult);

        var publicVars = collected.Where(v => v.Name.Contains("PublicResult")).ToList();
        var secretVars = collected.Where(v => v.Name.Contains("SecretResult")).ToList();

        publicVars.ShouldAllBe(v => !v.IsSensitive);
        secretVars.ShouldAllBe(v => v.IsSensitive);
    }

    [Fact]
    public void CollectOutputVariables_QualifiedAndUnqualified_BothGetSensitiveFlag()
    {
        var actionResult = new ActionExecutionResult
        {
            ExecutionMode = ExecutionMode.DirectScript,
            OutputVariables = new Dictionary<string, string>
            {
                ["Token"] = "secret"
            },
            SensitiveOutputVariableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Token"
            }
        };

        var collected = SimulateCollectOutputVariables("Deploy", actionResult);

        collected.Count.ShouldBe(2);

        var qualified = collected.First(v => v.Name.Contains("[Deploy]"));
        var unqualified = collected.First(v => v.Name == "Token");

        qualified.IsSensitive.ShouldBeTrue();
        unqualified.IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public void CollectOutputVariables_NonSensitive_IsSensitiveFalse()
    {
        var actionResult = new ActionExecutionResult
        {
            ExecutionMode = ExecutionMode.DirectScript,
            OutputVariables = new Dictionary<string, string>
            {
                ["Count"] = "42"
            },
            SensitiveOutputVariableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

        var collected = SimulateCollectOutputVariables("Step1", actionResult);

        collected.ShouldAllBe(v => !v.IsSensitive);
    }

    // ========== Mixed Scenario ==========

    [Fact]
    public void FullPipeline_MixedSensitiveAndPublic_CorrectlyLabeled()
    {
        var actionResult = new ActionExecutionResult
        {
            ExecutionMode = ExecutionMode.DirectScript
        };

        var lines = new List<string>
        {
            "##squid[setVariable name='ApiUrl' value='https://api.example.com']",
            "##squid[setVariable name='ApiKey' value='key-abc' sensitive='True']",
            "##squid[setVariable name='DebugMode' value='true' sensitive='False']"
        };

        SimulateCaptureOutputVariables(actionResult, lines);

        var collected = SimulateCollectOutputVariables("Deploy", actionResult);

        var apiUrl = collected.Where(v => v.Name.Contains("ApiUrl")).ToList();
        var apiKey = collected.Where(v => v.Name.Contains("ApiKey")).ToList();
        var debug = collected.Where(v => v.Name.Contains("DebugMode")).ToList();

        apiUrl.ShouldAllBe(v => !v.IsSensitive);
        apiKey.ShouldAllBe(v => v.IsSensitive);
        debug.ShouldAllBe(v => !v.IsSensitive);
    }

    // ========== Helpers (mirror executor logic) ==========

    private static void SimulateCaptureOutputVariables(ActionExecutionResult actionResult, List<string> logLines)
    {
        var outputVars = ServiceMessageParser.ParseOutputVariables(logLines);

        foreach (var kv in outputVars)
        {
            actionResult.OutputVariables[kv.Key] = kv.Value.Value;

            if (kv.Value.IsSensitive)
                actionResult.SensitiveOutputVariableNames.Add(kv.Key);
        }
    }

    private static List<Message.Models.Deployments.Variable.VariableDto> SimulateCollectOutputVariables(
        string stepName, ActionExecutionResult actionResult)
    {
        var outputVariables = new List<Message.Models.Deployments.Variable.VariableDto>();

        foreach (var kv in actionResult.OutputVariables)
        {
            var isSensitive = actionResult.SensitiveOutputVariableNames.Contains(kv.Key);
            var qualifiedName = DeploymentVariables.Action.OutputVariable(stepName, kv.Key);

            outputVariables.Add(new() { Name = qualifiedName, Value = kv.Value, IsSensitive = isSensitive });
            outputVariables.Add(new() { Name = kv.Key, Value = kv.Value, IsSensitive = isSensitive });
        }

        return outputVariables;
    }
}

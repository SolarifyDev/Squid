using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Rendering.Adapters;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.Deployments.Execution.Rendering;

/// <summary>
/// Phase 5 — unit tests for <see cref="LegacyIntentAdapter"/>. Each ActionType maps to the
/// correct <see cref="ExecutionIntent"/> subtype with the expected fields propagated from
/// the legacy <see cref="ActionExecutionResult"/>.
/// </summary>
public class LegacyIntentAdapterTests
{
    [Fact]
    public void FromLegacyResult_NullResult_Throws()
    {
        Should.Throw<ArgumentNullException>(() => LegacyIntentAdapter.FromLegacyResult(null!, "step"));
    }

    [Fact]
    public void FromLegacyResult_ScriptActionType_BuildsRunScriptIntent()
    {
        var result = new ActionExecutionResult
        {
            ActionName = "deploy-web",
            ActionType = SpecialVariables.ActionTypes.Script,
            ScriptBody = "echo hello",
            Syntax = ScriptSyntax.Bash
        };

        var intent = LegacyIntentAdapter.FromLegacyResult(result, "Run Script");

        intent.ShouldBeOfType<RunScriptIntent>();
        var run = (RunScriptIntent)intent;
        run.Name.ShouldBe("legacy:Squid.Script");
        run.StepName.ShouldBe("Run Script");
        run.ActionName.ShouldBe("deploy-web");
        run.ScriptBody.ShouldBe("echo hello");
        run.Syntax.ShouldBe(ScriptSyntax.Bash);
        run.InjectRuntimeBundle.ShouldBeFalse();
        run.Assets.ShouldBeEmpty();
    }

    [Fact]
    public void FromLegacyResult_HealthCheckActionType_BuildsHealthCheckIntent_WithEmptyScriptBody()
    {
        var result = new ActionExecutionResult
        {
            ActionName = "hc",
            ActionType = SpecialVariables.ActionTypes.HealthCheck,
            ScriptBody = null
        };

        var intent = LegacyIntentAdapter.FromLegacyResult(result, "Health");

        intent.ShouldBeOfType<HealthCheckIntent>();
        var hc = (HealthCheckIntent)intent;
        hc.Name.ShouldBe("legacy:Squid.HealthCheck");
        hc.StepName.ShouldBe("Health");
        hc.ActionName.ShouldBe("hc");
        hc.CustomScript.ShouldBeNull();
    }

    [Fact]
    public void FromLegacyResult_HealthCheckActionType_CarriesCustomScript()
    {
        var result = new ActionExecutionResult
        {
            ActionName = "hc",
            ActionType = SpecialVariables.ActionTypes.HealthCheck,
            ScriptBody = "curl --fail http://x"
        };

        var intent = LegacyIntentAdapter.FromLegacyResult(result, "Health");

        var hc = intent.ShouldBeOfType<HealthCheckIntent>();
        hc.CustomScript.ShouldBe("curl --fail http://x");
    }

    [Fact]
    public void FromLegacyResult_HelmChartUpgrade_BuildsHelmUpgradeIntent_WithActionNameAsReleaseName()
    {
        var result = new ActionExecutionResult
        {
            ActionName = "my-release",
            ActionType = SpecialVariables.ActionTypes.HelmChartUpgrade,
            Files = new Dictionary<string, byte[]>
            {
                ["values.yaml"] = new byte[] { 1, 2, 3 }
            }
        };

        var intent = LegacyIntentAdapter.FromLegacyResult(result, "Upgrade");

        var helm = intent.ShouldBeOfType<HelmUpgradeIntent>();
        helm.Name.ShouldBe("legacy:Squid.HelmChartUpgrade");
        helm.ReleaseName.ShouldBe("my-release");
        helm.ChartReference.ShouldBe(string.Empty);
        helm.Namespace.ShouldBe(string.Empty);
        helm.Assets.Count.ShouldBe(1);
        helm.Assets[0].RelativePath.ShouldBe("values.yaml");
    }

    [Theory]
    [InlineData(SpecialVariables.ActionTypes.KubernetesDeployRawYaml)]
    [InlineData(SpecialVariables.ActionTypes.KubernetesDeployContainers)]
    [InlineData(SpecialVariables.ActionTypes.KubernetesDeployIngress)]
    [InlineData(SpecialVariables.ActionTypes.KubernetesDeployService)]
    [InlineData(SpecialVariables.ActionTypes.KubernetesDeployConfigMap)]
    [InlineData(SpecialVariables.ActionTypes.KubernetesDeploySecret)]
    [InlineData(SpecialVariables.ActionTypes.KubernetesKustomize)]
    public void FromLegacyResult_KubernetesActionTypes_BuildKubernetesApplyIntent(string actionType)
    {
        var result = new ActionExecutionResult
        {
            ActionName = "deploy",
            ActionType = actionType,
            Files = new Dictionary<string, byte[]>
            {
                ["deployment.yaml"] = new byte[] { 10, 20 },
                ["service.yaml"] = new byte[] { 30 }
            }
        };

        var intent = LegacyIntentAdapter.FromLegacyResult(result, "Apply");

        var apply = intent.ShouldBeOfType<KubernetesApplyIntent>();
        apply.Name.ShouldBe($"legacy:{actionType}");
        apply.StepName.ShouldBe("Apply");
        apply.ActionName.ShouldBe("deploy");
        apply.YamlFiles.Count.ShouldBe(2);
        apply.Assets.Count.ShouldBe(2);
        apply.ServerSideApply.ShouldBeFalse();
        apply.Namespace.ShouldBe(string.Empty);
    }

    [Fact]
    public void FromLegacyResult_UnknownActionType_FallsBackToRunScriptIntent()
    {
        var result = new ActionExecutionResult
        {
            ActionName = "mystery",
            ActionType = "Squid.UnknownNewAction",
            ScriptBody = "whoami"
        };

        var intent = LegacyIntentAdapter.FromLegacyResult(result, "Step");

        var run = intent.ShouldBeOfType<RunScriptIntent>();
        run.Name.ShouldBe("legacy:Squid.UnknownNewAction");
        run.ScriptBody.ShouldBe("whoami");
    }

    [Fact]
    public void FromLegacyResult_NullActionType_FallsBackToRunScriptIntent_WithUnknownName()
    {
        var result = new ActionExecutionResult
        {
            ActionName = "a",
            ActionType = null,
            ScriptBody = "echo"
        };

        var intent = LegacyIntentAdapter.FromLegacyResult(result, "Step");

        intent.Name.ShouldBe("legacy:unknown");
        intent.ShouldBeOfType<RunScriptIntent>();
    }

    [Fact]
    public void FromLegacyResult_NullFiles_ProducesEmptyAssets()
    {
        var result = new ActionExecutionResult
        {
            ActionName = "a",
            ActionType = SpecialVariables.ActionTypes.Script,
            ScriptBody = "echo",
            Files = null
        };

        var intent = LegacyIntentAdapter.FromLegacyResult(result, "Step");

        intent.Assets.ShouldBeEmpty();
    }
}

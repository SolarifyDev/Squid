using System.Linq;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.Deployments.Execution.Intents;

public class ExecutionIntentTests
{
    // ------------------------------------------------------------------
    // Base record — required fields, defaults, with-expression immutability
    // ------------------------------------------------------------------

    [Fact]
    public void RunScriptIntent_RequiresNameAndScriptBody()
    {
        var intent = new RunScriptIntent
        {
            Name = "run-script",
            ScriptBody = "echo hello"
        };

        intent.Name.ShouldBe("run-script");
        intent.ScriptBody.ShouldBe("echo hello");
        intent.Syntax.ShouldBe(ScriptSyntax.Bash);
        intent.InjectRuntimeBundle.ShouldBeTrue();
        intent.StepName.ShouldBe(string.Empty);
        intent.ActionName.ShouldBe(string.Empty);
        intent.Assets.ShouldBeEmpty();
        intent.RequiredCapabilities.ShouldBeEmpty();
        intent.Packages.ShouldBeEmpty();
        intent.Timeout.ShouldBeNull();
    }

    [Fact]
    public void WithExpression_ProducesModifiedCopy_LeavesOriginalUntouched()
    {
        var original = new RunScriptIntent
        {
            Name = "run-script",
            ScriptBody = "echo hi",
            StepName = "Step One"
        };

        var modified = original with { ScriptBody = "echo bye", InjectRuntimeBundle = false };

        modified.ShouldNotBeSameAs(original);
        modified.ScriptBody.ShouldBe("echo bye");
        modified.InjectRuntimeBundle.ShouldBeFalse();
        modified.StepName.ShouldBe("Step One");

        original.ScriptBody.ShouldBe("echo hi");
        original.InjectRuntimeBundle.ShouldBeTrue();
    }

    [Fact]
    public void RecordEquality_SameFieldValuesAreEqual_DifferentValuesAreNotEqual()
    {
        var a = new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            YamlFiles = new[] { DeploymentFile.Asset("content/deploy.yaml", new byte[] { 1, 2, 3 }) },
            Namespace = "prod"
        };

        var b = a with { };
        var c = a with { Namespace = "staging" };

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ShouldNotBe(c);
    }

    // ------------------------------------------------------------------
    // Base record — cross-cutting fields populate on all subtypes
    // ------------------------------------------------------------------

    [Fact]
    public void BaseFields_ArePopulated_OnAnyConcreteIntent()
    {
        var asset = DeploymentFile.Asset("content/data.json", new byte[] { 0xFF });

        var capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            IntentCapabilityKeys.Kubectl,
            IntentCapabilityKeys.NestedFiles
        };

        var pkg = new IntentPackageReference
        {
            PackageId = "Acme.Web",
            Version = "1.2.3",
            FeedId = "feeds-builtin"
        };

        var intent = new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            StepName = "Deploy web",
            ActionName = "Apply manifests",
            YamlFiles = new[] { asset },
            Namespace = "prod",
            Assets = new[] { asset },
            RequiredCapabilities = capabilities,
            Packages = new[] { pkg },
            Timeout = TimeSpan.FromMinutes(5)
        };

        intent.StepName.ShouldBe("Deploy web");
        intent.ActionName.ShouldBe("Apply manifests");
        intent.Assets.Single().ShouldBe(asset);
        intent.RequiredCapabilities.ShouldContain(IntentCapabilityKeys.Kubectl);
        intent.RequiredCapabilities.ShouldContain(IntentCapabilityKeys.NestedFiles);
        intent.Packages.Single().ShouldBe(pkg);
        intent.Timeout.ShouldBe(TimeSpan.FromMinutes(5));
    }

    // ------------------------------------------------------------------
    // Concrete subtypes
    // ------------------------------------------------------------------

    [Fact]
    public void DeployPackageIntent_CarriesPackageReferenceAndScripts()
    {
        var intent = new DeployPackageIntent
        {
            Name = "deploy-package",
            Package = new IntentPackageReference
            {
                PackageId = "Acme.Web",
                Version = "2.0.0",
                FeedId = "docker-hub",
                PurposeHint = "primary"
            },
            ExtractTo = "content/app",
            PreDeployScript = "echo before",
            PostDeployScript = "echo after",
            ScriptSyntax = ScriptSyntax.Bash
        };

        intent.Package.PackageId.ShouldBe("Acme.Web");
        intent.Package.PurposeHint.ShouldBe("primary");
        intent.ExtractTo.ShouldBe("content/app");
        intent.PreDeployScript.ShouldBe("echo before");
        intent.PostDeployScript.ShouldBe("echo after");
    }

    [Fact]
    public void KubernetesApplyIntent_DefaultsServerSideApplyFalseAndEmptyNamespace()
    {
        var intent = new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            YamlFiles = new[] { DeploymentFile.Asset("content/svc.yaml", new byte[] { 1 }) }
        };

        intent.Namespace.ShouldBe(string.Empty);
        intent.ServerSideApply.ShouldBeFalse();
        intent.YamlFiles.Count.ShouldBe(1);
    }

    [Fact]
    public void HelmUpgradeIntent_DefaultsInlineValuesToEmptyDictionary()
    {
        var intent = new HelmUpgradeIntent
        {
            Name = "helm-upgrade",
            ReleaseName = "web",
            ChartReference = "oci://registry.local/web"
        };

        intent.InlineValues.ShouldNotBeNull();
        intent.InlineValues.Count.ShouldBe(0);
        intent.ValuesFiles.ShouldBeEmpty();
        intent.Namespace.ShouldBe(string.Empty);
        intent.ChartVersion.ShouldBe(string.Empty);
        intent.CustomHelmExecutable.ShouldBe(string.Empty);
        intent.ResetValues.ShouldBeTrue();
        intent.Wait.ShouldBeFalse();
        intent.WaitForJobs.ShouldBeFalse();
        intent.Timeout.ShouldBe(string.Empty);
        intent.AdditionalArgs.ShouldBe(string.Empty);
        intent.Repository.ShouldBeNull();
    }

    [Fact]
    public void HealthCheckIntent_AllowsNullCustomScript_UsesBashSyntaxByDefault()
    {
        var defaultIntent = new HealthCheckIntent { Name = "health-check" };
        defaultIntent.CustomScript.ShouldBeNull();
        defaultIntent.Syntax.ShouldBe(ScriptSyntax.Bash);
        defaultIntent.CheckType.ShouldBe(HealthCheckType.FullHealthCheck);
        defaultIntent.ErrorHandling.ShouldBe(HealthCheckErrorHandling.FailDeployment);
        defaultIntent.IncludeNewTargets.ShouldBeFalse();

        var customIntent = defaultIntent with
        {
            CustomScript = "exit 0",
            CheckType = HealthCheckType.ConnectionTest,
            ErrorHandling = HealthCheckErrorHandling.SkipUnavailable,
            IncludeNewTargets = true
        };
        customIntent.CustomScript.ShouldBe("exit 0");
        customIntent.CheckType.ShouldBe(HealthCheckType.ConnectionTest);
        customIntent.ErrorHandling.ShouldBe(HealthCheckErrorHandling.SkipUnavailable);
        customIntent.IncludeNewTargets.ShouldBeTrue();
    }

    [Fact]
    public void ManualInterventionIntent_DefaultsInstructionsToEmpty_AndResponsibleTeamIdsToNull()
    {
        var intent = new ManualInterventionIntent { Name = "manual-intervention" };

        intent.Instructions.ShouldBe(string.Empty);
        intent.ResponsibleTeamIds.ShouldBeNull();
    }

    [Fact]
    public void ManualInterventionIntent_PreservesInstructionsAndTeamIds()
    {
        var intent = new ManualInterventionIntent
        {
            Name = "manual-intervention",
            Instructions = "Approve the release before continuing.",
            ResponsibleTeamIds = "team-releases,team-sre"
        };

        intent.Instructions.ShouldBe("Approve the release before continuing.");
        intent.ResponsibleTeamIds.ShouldBe("team-releases,team-sre");
    }

    [Fact]
    public void OpenClawInvokeIntent_DefaultsParametersToEmptyDictionary()
    {
        var intent = new OpenClawInvokeIntent
        {
            Name = "openclaw-wake",
            Kind = OpenClawInvocationKind.Wake
        };

        intent.Kind.ShouldBe(OpenClawInvocationKind.Wake);
        intent.Parameters.ShouldNotBeNull();
        intent.Parameters.Count.ShouldBe(0);
    }

    [Fact]
    public void OpenClawInvokeIntent_PreservesParameters()
    {
        var intent = new OpenClawInvokeIntent
        {
            Name = "openclaw-invoke-tool",
            Kind = OpenClawInvocationKind.InvokeTool,
            Parameters = new Dictionary<string, string>
            {
                ["Squid.Action.OpenClaw.Tool"] = "sessions_list",
                ["Squid.Action.OpenClaw.ToolAction"] = "json"
            }
        };

        intent.Parameters.Count.ShouldBe(2);
        intent.Parameters["Squid.Action.OpenClaw.Tool"].ShouldBe("sessions_list");
        intent.Parameters["Squid.Action.OpenClaw.ToolAction"].ShouldBe("json");
    }

    // ------------------------------------------------------------------
    // IntentCapabilityKeys drift / self-consistency
    // ------------------------------------------------------------------

    [Fact]
    public void IntentCapabilityKeys_All_ContainsEveryConstant()
    {
        var declared = typeof(IntentCapabilityKeys)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        declared.Length.ShouldBeGreaterThan(0);

        foreach (var key in declared)
            IntentCapabilityKeys.All.ShouldContain(key);

        IntentCapabilityKeys.All.Count.ShouldBe(declared.Length);
    }

    [Fact]
    public void IntentCapabilityKeys_All_IsCaseInsensitive()
    {
        IntentCapabilityKeys.All.ShouldContain("BASH");
        IntentCapabilityKeys.All.ShouldContain("Kubectl");
        IntentCapabilityKeys.All.ShouldContain("nested-files");
    }
}

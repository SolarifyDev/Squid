using System.Linq;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.Deployments.Execution.Validation;

/// <summary>
/// Phase 5.5 — unit tests for <see cref="CapabilityValidator"/>. Each violation code is
/// exercised in isolation and the multi-violation accumulation path asserts that every
/// check is independent (no short-circuiting).
/// </summary>
public class CapabilityValidatorTests
{
    private readonly CapabilityValidator _validator = new();

    // ---------- guard clauses -------------------------------------------

    [Fact]
    public void Validate_NullIntent_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            _validator.Validate(null!, BuildCapabilities(), CommunicationStyle.Ssh));
    }

    [Fact]
    public void Validate_NullCapabilities_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            _validator.Validate(BuildRunScriptIntent(), null!, CommunicationStyle.Ssh));
    }

    // ---------- happy path ----------------------------------------------

    [Fact]
    public void Validate_FullySupportedIntent_ReturnsEmptyList()
    {
        var intent = BuildRunScriptIntent();
        var caps = BuildCapabilities(
            syntaxes: new[] { ScriptSyntax.Bash },
            supportsNestedFiles: true,
            features: new[] { IntentCapabilityKeys.Bash });

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh);

        result.ShouldBeEmpty();
    }

    // ---------- UNSUPPORTED_ACTION_TYPE ---------------------------------

    [Fact]
    public void Validate_ActionTypeMissingFromSupportedSet_EmitsViolation()
    {
        var intent = BuildRunScriptIntent();
        var caps = BuildCapabilities(
            syntaxes: new[] { ScriptSyntax.Bash },
            actionTypes: new[] { "Squid.Script" });

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh, actionType: "Squid.HelmChartUpgrade");

        var violation = result.ShouldHaveSingleItem();
        violation.Code.ShouldBe(ViolationCodes.UnsupportedActionType);
        violation.Detail.ShouldBe("Squid.HelmChartUpgrade");
        violation.CommunicationStyle.ShouldBe(CommunicationStyle.Ssh);
        violation.IntentName.ShouldBe(intent.Name);
    }

    [Fact]
    public void Validate_ActionTypeWithinSupportedSet_NoViolation()
    {
        var intent = BuildRunScriptIntent();
        var caps = BuildCapabilities(
            syntaxes: new[] { ScriptSyntax.Bash },
            actionTypes: new[] { "Squid.Script" });

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh, actionType: "Squid.Script");

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_EmptySupportedActionTypes_IsTreatedAsWildcard()
    {
        var intent = BuildRunScriptIntent();
        var caps = BuildCapabilities(
            syntaxes: new[] { ScriptSyntax.Bash },
            actionTypes: Array.Empty<string>());

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh, actionType: "Squid.AnythingNew");

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_NullActionType_SkipsActionTypeCheck()
    {
        var intent = BuildRunScriptIntent();
        var caps = BuildCapabilities(
            syntaxes: new[] { ScriptSyntax.Bash },
            actionTypes: new[] { "Squid.Script" });

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh, actionType: null);

        result.ShouldBeEmpty();
    }

    // ---------- UNSUPPORTED_SYNTAX --------------------------------------

    [Fact]
    public void Validate_RunScriptIntent_SyntaxNotSupported_EmitsViolation()
    {
        var intent = BuildRunScriptIntent(syntax: ScriptSyntax.PowerShell);
        var caps = BuildCapabilities(syntaxes: new[] { ScriptSyntax.Bash });

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh);

        var violation = result.ShouldHaveSingleItem();
        violation.Code.ShouldBe(ViolationCodes.UnsupportedSyntax);
        violation.Detail.ShouldBe(nameof(ScriptSyntax.PowerShell));
    }

    [Fact]
    public void Validate_DeployPackageIntent_UsesScriptSyntaxForCheck()
    {
        var intent = new DeployPackageIntent
        {
            Name = "deploy-package",
            Package = new IntentPackageReference { PackageId = "Acme.Web", Version = "1.0.0", FeedId = "feeds-builtin" },
            ScriptSyntax = ScriptSyntax.Python
        };

        var caps = BuildCapabilities(
            syntaxes: new[] { ScriptSyntax.Bash },
            stagingModes: PackageStagingMode.UploadOnly);

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh);

        result.ShouldContain(v => v.Code == ViolationCodes.UnsupportedSyntax && v.Detail == nameof(ScriptSyntax.Python));
    }

    [Fact]
    public void Validate_HealthCheckIntent_WithoutCustomScript_SkipsSyntaxCheck()
    {
        var intent = new HealthCheckIntent { Name = "health" };
        var caps = BuildCapabilities(syntaxes: new[] { ScriptSyntax.PowerShell });

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_HealthCheckIntent_WithCustomScript_RunsSyntaxCheck()
    {
        var intent = new HealthCheckIntent
        {
            Name = "health",
            CustomScript = "curl --fail http://x",
            Syntax = ScriptSyntax.Bash
        };

        var caps = BuildCapabilities(syntaxes: new[] { ScriptSyntax.PowerShell });

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh);

        result.ShouldContain(v => v.Code == ViolationCodes.UnsupportedSyntax);
    }

    // ---------- NESTED_FILES --------------------------------------------

    [Fact]
    public void Validate_NestedAsset_OnTransportWithoutNestedSupport_EmitsViolationPerPath()
    {
        var intent = BuildRunScriptIntent(assets: new[]
        {
            DeploymentFile.Asset("flat.yaml", new byte[] { 1 }),
            DeploymentFile.Asset("content/deploy.yaml", new byte[] { 2 }),
            DeploymentFile.Asset("content/service.yaml", new byte[] { 3 })
        });

        var caps = BuildCapabilities(
            syntaxes: new[] { ScriptSyntax.Bash },
            supportsNestedFiles: false);

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh);

        var nested = result.Where(v => v.Code == ViolationCodes.NestedFiles).ToArray();
        nested.Length.ShouldBe(2);
        nested.Select(v => v.Detail).ShouldBe(new[] { "content/deploy.yaml", "content/service.yaml" }, ignoreOrder: true);
    }

    [Fact]
    public void Validate_NestedAsset_OnTransportWithNestedSupport_NoViolation()
    {
        var intent = BuildRunScriptIntent(assets: new[]
        {
            DeploymentFile.Asset("content/a/b/c.yaml", new byte[] { 1 })
        });

        var caps = BuildCapabilities(
            syntaxes: new[] { ScriptSyntax.Bash },
            supportsNestedFiles: true);

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh);

        result.ShouldBeEmpty();
    }

    // ---------- MISSING_FEATURE -----------------------------------------

    [Fact]
    public void Validate_RequiredCapabilityMissing_EmitsViolationPerMissingKey()
    {
        var intent = BuildRunScriptIntent(requiredCapabilities: new[]
        {
            IntentCapabilityKeys.Bash,
            IntentCapabilityKeys.Kubectl,
            IntentCapabilityKeys.Helm
        });

        var caps = BuildCapabilities(
            syntaxes: new[] { ScriptSyntax.Bash },
            features: new[] { IntentCapabilityKeys.Bash });

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh);

        var missing = result.Where(v => v.Code == ViolationCodes.MissingFeature).ToArray();
        missing.Length.ShouldBe(2);
        missing.Select(v => v.Detail).ShouldBe(new[] { IntentCapabilityKeys.Kubectl, IntentCapabilityKeys.Helm }, ignoreOrder: true);
    }

    [Fact]
    public void Validate_RequiredCapabilityPresent_CaseInsensitiveMatch_NoViolation()
    {
        var intent = BuildRunScriptIntent(requiredCapabilities: new[] { "BASH", "KUBECTL" });
        var caps = BuildCapabilities(
            syntaxes: new[] { ScriptSyntax.Bash },
            features: new[] { "bash", "kubectl" });

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh);

        result.ShouldBeEmpty();
    }

    // ---------- PACKAGE_STAGING -----------------------------------------

    [Fact]
    public void Validate_IntentWithPackages_OnTransportWithNoStaging_EmitsViolation()
    {
        var intent = BuildRunScriptIntent(packages: new[]
        {
            new IntentPackageReference { PackageId = "Acme.Web", Version = "1.0.0", FeedId = "feeds-builtin" }
        });

        var caps = BuildCapabilities(
            syntaxes: new[] { ScriptSyntax.Bash },
            stagingModes: PackageStagingMode.None);

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh);

        var violation = result.Single(v => v.Code == ViolationCodes.PackageStaging);
        violation.Message.ShouldContain("package staging");
    }

    [Fact]
    public void Validate_DeployPackageIntent_OnTransportWithNoStaging_EmitsViolation()
    {
        var intent = new DeployPackageIntent
        {
            Name = "deploy-package",
            Package = new IntentPackageReference { PackageId = "Acme.Web", Version = "1.0.0", FeedId = "feeds-builtin" },
            ScriptSyntax = ScriptSyntax.Bash
        };

        var caps = BuildCapabilities(
            syntaxes: new[] { ScriptSyntax.Bash },
            stagingModes: PackageStagingMode.None);

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh);

        result.ShouldContain(v => v.Code == ViolationCodes.PackageStaging);
    }

    [Fact]
    public void Validate_IntentWithPackages_OnTransportWithUploadOnly_NoViolation()
    {
        var intent = BuildRunScriptIntent(packages: new[]
        {
            new IntentPackageReference { PackageId = "Acme.Web", Version = "1.0.0", FeedId = "feeds-builtin" }
        });

        var caps = BuildCapabilities(
            syntaxes: new[] { ScriptSyntax.Bash },
            stagingModes: PackageStagingMode.UploadOnly);

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh);

        result.ShouldBeEmpty();
    }

    // ---------- multi-violation accumulation ----------------------------

    [Fact]
    public void Validate_MultipleIndependentFailures_AllViolationsAccumulated()
    {
        var intent = BuildRunScriptIntent(
            syntax: ScriptSyntax.Python,
            assets: new[] { DeploymentFile.Asset("content/deploy.yaml", new byte[] { 1 }) },
            requiredCapabilities: new[] { IntentCapabilityKeys.Kubectl },
            packages: new[] { new IntentPackageReference { PackageId = "p", Version = "1", FeedId = "f" } });

        var caps = BuildCapabilities(
            syntaxes: new[] { ScriptSyntax.Bash },
            supportsNestedFiles: false,
            features: Array.Empty<string>(),
            stagingModes: PackageStagingMode.None,
            actionTypes: new[] { "Squid.Script" });

        var result = _validator.Validate(intent, caps, CommunicationStyle.Ssh, actionType: "Squid.HelmChartUpgrade");

        var codes = result.Select(v => v.Code).ToHashSet();
        codes.ShouldContain(ViolationCodes.UnsupportedActionType);
        codes.ShouldContain(ViolationCodes.UnsupportedSyntax);
        codes.ShouldContain(ViolationCodes.NestedFiles);
        codes.ShouldContain(ViolationCodes.MissingFeature);
        codes.ShouldContain(ViolationCodes.PackageStaging);
        codes.Count.ShouldBe(5);
    }

    // ---------- violation payload -------------------------------------

    [Fact]
    public void Validate_ViolationCarriesIntentAndTransportContext()
    {
        var intent = new RunScriptIntent
        {
            Name = "run-script",
            ScriptBody = "echo",
            StepName = "Deploy",
            ActionName = "Run",
            Syntax = ScriptSyntax.PowerShell
        };

        var caps = BuildCapabilities(syntaxes: new[] { ScriptSyntax.Bash });

        var result = _validator.Validate(intent, caps, CommunicationStyle.KubernetesApi);

        var violation = result.ShouldHaveSingleItem();
        violation.IntentName.ShouldBe("run-script");
        violation.StepName.ShouldBe("Deploy");
        violation.ActionName.ShouldBe("Run");
        violation.CommunicationStyle.ShouldBe(CommunicationStyle.KubernetesApi);
    }

    // ---------- helpers -------------------------------------------------

    private static RunScriptIntent BuildRunScriptIntent(
        ScriptSyntax syntax = ScriptSyntax.Bash,
        IEnumerable<DeploymentFile>? assets = null,
        IEnumerable<string>? requiredCapabilities = null,
        IEnumerable<IntentPackageReference>? packages = null) => new()
    {
        Name = "run-script",
        ScriptBody = "echo hello",
        Syntax = syntax,
        Assets = (assets ?? Array.Empty<DeploymentFile>()).ToArray(),
        RequiredCapabilities = new HashSet<string>(requiredCapabilities ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
        Packages = (packages ?? Array.Empty<IntentPackageReference>()).ToArray()
    };

    private static ITransportCapabilities BuildCapabilities(
        IEnumerable<ScriptSyntax>? syntaxes = null,
        bool supportsNestedFiles = false,
        IEnumerable<string>? features = null,
        PackageStagingMode stagingModes = PackageStagingMode.UploadOnly,
        IEnumerable<string>? actionTypes = null) => new TransportCapabilities
    {
        SupportedSyntaxes = new HashSet<ScriptSyntax>(syntaxes ?? Array.Empty<ScriptSyntax>()),
        SupportsNestedFiles = supportsNestedFiles,
        OptionalFeatures = new HashSet<string>(features ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
        PackageStagingModes = stagingModes,
        SupportedActionTypes = new HashSet<string>(actionTypes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase)
    };
}

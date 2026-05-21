using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Shouldly;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Execution.Validation;

/// <summary>
/// Drift guards for <see cref="ViolationCodes"/>. The <see cref="ViolationCodes.All"/>
/// HashSet is consumed by the preview UI and by any future telemetry that wants to
/// distinguish known violation kinds from unknown ones; an emitted code that's not
/// in <c>All</c> would silently slip through.
///
/// <para>Adding a new violation code without updating <c>All</c> would be invisible
/// to every existing test — these tests force the contract:</para>
///
/// <list type="bullet">
///   <item><c>All_ContainsEveryPublicConst</c> — reflection pins that every
///         public const string in the class is in <c>All</c>.</item>
///   <item><c>All_CardinalityPinned</c> — explicit count check so a code added
///         without a matching test update fails CI loudly.</item>
///   <item><c>EmittedCodes_AreAllInAll</c> — drives every validator branch and
///         asserts every emitted <c>CapabilityViolation.Code</c> is in <c>All</c>.</item>
/// </list>
/// </summary>
public class ViolationCodesIntegrityTests
{
    [Fact]
    public void All_ContainsEveryPublicConst()
    {
        // Reflection-based drift detector: anyone who adds a new public const must
        // also include it in `All`, or this test fails on the very next build.
        var publicConsts = typeof(ViolationCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        publicConsts.ShouldNotBeEmpty(
            customMessage: "Reflection found zero public string consts on ViolationCodes — class shape unexpectedly changed.");

        foreach (var literal in publicConsts)
        {
            ViolationCodes.All.ShouldContain(literal,
                customMessage:
                    $"Constant '{literal}' is declared on ViolationCodes but NOT in ViolationCodes.All. " +
                    "Add it to the All HashSet — otherwise downstream UI / telemetry treats it as " +
                    "an unknown violation.");
        }
    }

    [Fact]
    public void All_CardinalityPinned()
    {
        // Hard-pin the cardinality. Adding a violation code requires bumping this
        // number — surfaces "did you forget to update tests?" at build time
        // rather than during a production incident.
        ViolationCodes.All.Count.ShouldBe(6,
            customMessage:
                "ViolationCodes.All cardinality changed. If you added a new violation, also: " +
                "(1) update this pin to the new count, (2) add a corresponding test in " +
                "CapabilityValidator that exercises the new code path, (3) verify the preview UI " +
                "translates the new code to a useful message.");
    }

    [Fact]
    public void All_ExpectedCodesPresent_Pinned()
    {
        // Each well-known violation code spelled out so a typo in either the
        // constant value or the All set is caught.
        ViolationCodes.All.ShouldContain(ViolationCodes.UnsupportedActionType);
        ViolationCodes.All.ShouldContain(ViolationCodes.UnsupportedSyntax);
        ViolationCodes.All.ShouldContain(ViolationCodes.NestedFiles);
        ViolationCodes.All.ShouldContain(ViolationCodes.MissingFeature);
        ViolationCodes.All.ShouldContain(ViolationCodes.PackageStaging);
        ViolationCodes.All.ShouldContain(ViolationCodes.MissingCapability);
    }

    [Fact]
    public void EmittedCodes_FromValidate_AreAllInAll()
    {
        // Run the existing 5-dimension Validate against scenarios that force each
        // distinct violation code, then assert every emitted Code is in All.
        // If a future PR adds a new emitter without updating All, this fails.
        var validator = new CapabilityValidator();
        var emittedCodes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scenario in BuildValidateScenarios())
        {
            var violations = validator.Validate(
                scenario.Intent, scenario.Capabilities, CommunicationStyle.Unknown, scenario.ActionType);

            foreach (var v in violations) emittedCodes.Add(v.Code);
        }

        emittedCodes.ShouldNotBeEmpty(
            customMessage: "Validate scenarios should have produced at least one violation; check the test scenarios.");

        foreach (var code in emittedCodes)
        {
            ViolationCodes.All.ShouldContain(code,
                customMessage:
                    $"CapabilityValidator.Validate emitted code '{code}' but it's not in ViolationCodes.All. " +
                    "Either add the const to ViolationCodes.All or stop emitting this code.");
        }
    }

    [Fact]
    public void EmittedCodes_FromValidateStaticRequirements_AreAllInAll()
    {
        var validator = new CapabilityValidator();
        var emittedCodes = new HashSet<string>(StringComparer.Ordinal);

        // Slot mismatch — handler accepts Windows, target advertises Linux.
        var reqs = CapabilityRequirements.Empty.Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows);
        var caps = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [CapabilityKeys.OsSlot] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { CapabilityKeys.Os.Linux }
        };

        var violations = validator.ValidateStaticRequirements(
            reqs, caps, BuildIntent(), CommunicationStyle.Unknown);

        foreach (var v in violations) emittedCodes.Add(v.Code);

        // Slot missing — handler requires shell:powershell, target has different shell.
        var reqs2 = CapabilityRequirements.Empty.Require(CapabilityKeys.Shell.PowerShell, CapabilityKeys.Present);
        var caps2 = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [CapabilityKeys.OsSlot] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { CapabilityKeys.Os.Linux }
        };

        var violations2 = validator.ValidateStaticRequirements(
            reqs2, caps2, BuildIntent(), CommunicationStyle.Unknown);

        foreach (var v in violations2) emittedCodes.Add(v.Code);

        emittedCodes.ShouldNotBeEmpty();

        foreach (var code in emittedCodes)
        {
            ViolationCodes.All.ShouldContain(code,
                customMessage:
                    $"CapabilityValidator.ValidateStaticRequirements emitted code '{code}' but it's not in ViolationCodes.All.");
        }
    }

    private static ExecutionIntent BuildIntent() => new RunScriptIntent
    {
        Name = "test", StepName = "S", ActionName = "A", ScriptBody = "echo", Syntax = ScriptSyntax.Bash
    };

    private static IEnumerable<(ExecutionIntent Intent, ITransportCapabilities Capabilities, string ActionType)> BuildValidateScenarios()
    {
        var bashOnly = new TransportCapabilities { SupportedSyntaxes = TransportCapabilities.Syntaxes(ScriptSyntax.Bash) };
        var restrictiveActionTypes = new TransportCapabilities
        {
            SupportedSyntaxes = TransportCapabilities.Syntaxes(ScriptSyntax.Bash),
            SupportedActionTypes = TransportCapabilities.ActionTypes("Squid.Other"),
        };

        // Scenario 1: UnsupportedActionType — transport's SupportedActionTypes set is non-empty + doesn't include action type
        yield return (
            new RunScriptIntent { Name = "n", StepName = "S", ActionName = "A", ScriptBody = "x", Syntax = ScriptSyntax.Bash },
            restrictiveActionTypes,
            "Squid.Unknown"
        );

        // Scenario 2: UnsupportedSyntax — transport supports Bash; intent uses PowerShell
        yield return (
            new RunScriptIntent { Name = "n", StepName = "S", ActionName = "A", ScriptBody = "x", Syntax = ScriptSyntax.PowerShell },
            bashOnly,
            null
        );

        // Scenario 3: NestedFiles — transport rejects, intent has asset with subpath
        var nestedIntent = new RunScriptIntent
        {
            Name = "n", StepName = "S", ActionName = "A", ScriptBody = "x", Syntax = ScriptSyntax.Bash,
            Assets = new List<DeploymentFile>
            {
                DeploymentFile.Asset("sub/dir/file.txt", System.Array.Empty<byte>())
            }
        };
        yield return (nestedIntent, bashOnly, null);

        // Scenario 4: MissingFeature — intent requires a feature transport doesn't declare
        var featureIntent = new RunScriptIntent
        {
            Name = "n", StepName = "S", ActionName = "A", ScriptBody = "x", Syntax = ScriptSyntax.Bash,
            RequiredCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { IntentCapabilityKeys.Kubectl }
        };
        yield return (featureIntent, bashOnly, null);

        // Scenario 5: PackageStaging — intent declares packages, transport has staging=None (default)
        var packageIntent = new RunScriptIntent
        {
            Name = "n", StepName = "S", ActionName = "A", ScriptBody = "x", Syntax = ScriptSyntax.Bash,
            Packages = new List<IntentPackageReference>
            {
                new() { PackageId = "pkg", Version = "1.0.0", FeedId = "1" }
            }
        };
        yield return (packageIntent, bashOnly, null);
    }
}

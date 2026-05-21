using System.Linq;
using Shouldly;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

public class IISDeployActionHandlerTests
{
    [Fact]
    public void ActionType_MatchesPublishedConstant()
    {
        // Renaming this constant breaks every customer who scripted against
        // the action type. Hard-pin so any drift is caught at build time.
        new IISDeployActionHandler().ActionType.ShouldBe("Squid.DeployToIISWebSite");
        new IISDeployActionHandler().ActionType.ShouldBe(SpecialVariables.ActionTypes.DeployToIISWebSite);
    }

    // ── Intent emission ────────────────────────────────────────────────────

    [Fact]
    public async Task DescribeIntentAsync_ReturnsRunScriptIntent_WithPowerShellSyntax()
    {
        var handler = (IActionHandler)new IISDeployActionHandler();
        var ctx = BuildContext(BuildAction(
            (IISDeployProperties.CreateOrUpdateWebSite, "True"),
            (IISDeployProperties.WebSiteName, "OrderApi")));

        var intent = await handler.DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ShouldBeOfType<RunScriptIntent>();
        var runScript = (RunScriptIntent)intent;

        runScript.Syntax.ShouldBe(ScriptSyntax.PowerShell);
        runScript.Name.ShouldBe("deploy-to-iis-website");
        // We DON'T inject Squid's bash-flavoured runtime bundle — the IIS script is
        // self-contained (mutex + retry + appcmd + netsh all live in the embedded body).
        runScript.InjectRuntimeBundle.ShouldBeFalse();
    }

    [Fact]
    public async Task DescribeIntentAsync_PropagatesStepAndActionNamesForObservability()
    {
        var handler = (IActionHandler)new IISDeployActionHandler();
        var ctx = BuildContext(
            BuildAction((IISDeployProperties.WebSiteName, "X")),
            stepName: "Deploy OrderApi",
            actionName: "IIS Website");

        var intent = (RunScriptIntent)await handler.DescribeIntentAsync(ctx, CancellationToken.None);

        intent.StepName.ShouldBe("Deploy OrderApi");
        intent.ActionName.ShouldBe("IIS Website");
    }

    [Fact]
    public async Task DescribeIntentAsync_ScriptBody_ContainsBothPreambleAndEmbeddedBody()
    {
        var handler = (IActionHandler)new IISDeployActionHandler();
        var ctx = BuildContext(BuildAction(
            (IISDeployProperties.WebSiteName, "OrderApi"),
            (IISDeployProperties.ApplicationPoolName, "OrderApi-Pool")));

        var intent = (RunScriptIntent)await handler.DescribeIntentAsync(ctx, CancellationToken.None);

        // Preamble assignment lands in the script
        intent.ScriptBody.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.WebSiteName'] = 'OrderApi'");

        // Embedded body's script-block declaration is also present
        intent.ScriptBody.ShouldContain("$DeployIISScriptBlock = {");
    }

    // ── OS guard ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Linux")]
    [InlineData("Darwin")]
    [InlineData("FreeBSD")]
    public async Task DescribeIntentAsync_NonWindowsTentacle_ThrowsWithActionableMessage(string os)
    {
        var handler = (IActionHandler)new IISDeployActionHandler();
        var ctx = BuildContext(
            action: BuildAction((IISDeployProperties.WebSiteName, "X")),
            stepName: "Deploy OrderApi",
            actionName: "IIS Website",
            tentacleOs: os);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => handler.DescribeIntentAsync(ctx, CancellationToken.None));

        // Message must include: the action type (so operators know which step), the actual OS
        // reported (so they know why), the remediation (configure a Windows Tentacle / refresh
        // health check). Rule 12.10: failure messages must be actionable.
        ex.Message.ShouldContain("Squid.DeployToIISWebSite");
        ex.Message.ShouldContain("Deploy OrderApi");
        ex.Message.ShouldContain("IIS Website");
        ex.Message.ShouldContain(os);
        ex.Message.ShouldContain("Windows Tentacle");
        ex.Message.ShouldContain("health check");
    }

    [Theory]
    // Canonical short form — current AgentOperatingSystems.Windows
    [InlineData("Windows")]
    [InlineData("windows")]   // case-insensitive — Windows reports vary
    [InlineData("WINDOWS")]
    // Legacy long form — older Tentacle binaries / cache entries seeded before the
    // canonical-constant refactor wrote Environment.OSVersion.VersionString into
    // the "os" metadata field. Real-world examples reported by operators below.
    // Without these the production-blocking failure mode lands: a fully-functional
    // Windows Tentacle gets rejected at dispatch with a misleading "not Windows" error.
    [InlineData("Microsoft Windows NT 10.0.19045.0")]    // Windows 10 22H2 (user's failure mode)
    [InlineData("Microsoft Windows NT 10.0.22631.0")]    // Windows 11 23H2
    [InlineData("Microsoft Windows NT 10.0.17763.0")]    // Windows Server 2019
    [InlineData("Microsoft Windows NT 10.0.20348.0")]    // Windows Server 2022
    [InlineData("microsoft windows nt 10.0.19045.0")]    // lowercased — defensive
    public async Task DescribeIntentAsync_WindowsTentacle_ProceedsSuccessfully(string os)
    {
        var handler = (IActionHandler)new IISDeployActionHandler();
        var ctx = BuildContext(
            BuildAction((IISDeployProperties.WebSiteName, "X")),
            tentacleOs: os);

        // Should not throw.
        var intent = await handler.DescribeIntentAsync(ctx, CancellationToken.None);
        intent.ShouldBeOfType<RunScriptIntent>();
    }

    [Theory]
    // Canonical short form
    [InlineData("Windows", true)]
    [InlineData("windows", true)]
    [InlineData("WINDOWS", true)]
    // Legacy long forms — Environment.OSVersion.VersionString on Windows
    [InlineData("Microsoft Windows NT 10.0.19045.0", true)]
    [InlineData("Microsoft Windows NT 6.3.9600.0", true)]    // legacy Windows 8.1 / Server 2012 R2
    [InlineData("microsoft windows nt 6.1.7601.0", true)]    // case-insensitive prefix
    // Non-Windows — must reject
    [InlineData("Linux", false)]
    [InlineData("linux", false)]
    [InlineData("macOS", false)]
    [InlineData("Darwin", false)]
    [InlineData("FreeBSD", false)]
    [InlineData("Unknown", false)]
    // Edge cases
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    // Anchoring guard: must NOT false-positive on a string that merely CONTAINS
    // "Windows" but isn't a Windows OS marker (the helper anchors on prefix,
    // not Contains, exactly to prevent this).
    [InlineData("LinuxOnWindowsSubsystem", false)]
    [InlineData("not-a-windows-host", false)]
    public void LooksLikeWindowsOsString_PinnedBehaviour(string osValue, bool expected)
    {
        // Pin both halves of the tolerance contract:
        //   1. canonical "Windows" → true
        //   2. legacy "Microsoft Windows ..." → true (the operator-blocking bug fix)
        //   3. explicit Linux/macOS/Unknown → false (we still reject these)
        //   4. unrelated strings that contain the word "Windows" → false
        //      (prevents accidental positives — the helper uses StartsWith, not Contains)
        IISDeployActionHandler.LooksLikeWindowsOsString(osValue).ShouldBe(expected,
            customMessage:
                $"LooksLikeWindowsOsString('{osValue}') expected {expected} but got {!expected}. " +
                $"If a legitimate Windows OS string is being rejected, extend the helper's prefix list. " +
                $"If a non-Windows string is being accepted, the prefix anchor has been weakened.");
    }

    [Fact]
    public async Task DescribeIntentAsync_OsCacheMiss_ProceedsOptimistically()
    {
        // A brand-new Tentacle that hasn't done a health check yet has no entry in the
        // runtime-capabilities cache → no Squid.Tentacle.OS variable contributed. The handler
        // proceeds; the agent-side script's `Get-WindowsFeature Web-WebServer` probe is the
        // final authority and will fail loudly on non-Windows.
        var handler = (IActionHandler)new IISDeployActionHandler();
        var ctx = BuildContext(
            BuildAction((IISDeployProperties.WebSiteName, "X")),
            tentacleOs: null);

        var intent = await handler.DescribeIntentAsync(ctx, CancellationToken.None);
        intent.ShouldBeOfType<RunScriptIntent>();
    }

    [Fact]
    public async Task DescribeIntentAsync_OsEmptyString_ProceedsOptimistically()
    {
        // The contributor only emits Squid.Tentacle.OS when caps.Os is non-empty
        // (see TentacleEndpointVariableContributor:59), but defensive against a future
        // refactor that could emit empty strings.
        var handler = (IActionHandler)new IISDeployActionHandler();
        var ctx = BuildContext(
            BuildAction((IISDeployProperties.WebSiteName, "X")),
            tentacleOs: "");

        var intent = await handler.DescribeIntentAsync(ctx, CancellationToken.None);
        intent.ShouldBeOfType<RunScriptIntent>();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static DeploymentActionDto BuildAction(params (string Name, string Value)[] properties)
    {
        return new DeploymentActionDto
        {
            Id = 1,
            Name = "IIS Website",
            ActionType = "Squid.DeployToIISWebSite",
            Properties = properties
                .Select(p => new DeploymentActionPropertyDto { PropertyName = p.Name, PropertyValue = p.Value })
                .ToList()
        };
    }

    private static ActionExecutionContext BuildContext(
        DeploymentActionDto action,
        string stepName = "Deploy",
        string actionName = "IIS Website",
        string tentacleOs = "Windows")
    {
        var variables = new List<VariableDto>();

        if (tentacleOs != null)
            variables.Add(new VariableDto { Name = "Squid.Tentacle.OS", Value = tentacleOs });

        return new ActionExecutionContext
        {
            Step = new DeploymentStepDto { Name = stepName },
            Action = new DeploymentActionDto
            {
                Id = action.Id,
                Name = actionName,
                ActionType = action.ActionType,
                Properties = action.Properties
            },
            Variables = variables
        };
    }
}

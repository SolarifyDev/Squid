using Shouldly;
using Squid.Calamari.Commands;
using Squid.Calamari.Commands.Configuration;
using Squid.Calamari.Commands.Conventions;
using Squid.Calamari.Commands.StructuredConfig;
using Squid.Calamari.Commands.Substitution;
using Squid.Calamari.Pipeline;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands;

/// <summary>
/// PR-5 — pins that every G1.x pipeline step appends a structured
/// <see cref="StepOutcome"/> to <c>context.StepOutcomes</c> when it
/// completes. The outcome record is part of the
/// <c>CommandExecutionResult.StepOutcomes</c> public contract — silent
/// regression here would mean UI / log-analytics consumers stop seeing
/// metrics for the affected step.
/// </summary>
public sealed class StepOutcomeEmissionTests : IDisposable
{
    private readonly string _workDir;

    public StepOutcomeEmissionTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"outcome-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public async Task SubstituteInFilesStep_EmitsOutcome_WithFileCounts()
    {
        File.WriteAllText(Path.Combine(_workDir, "a.txt"), "v=#{X}");

        var ctx = BuildContext();
        ctx.Variables!.Set(SubstituteInFilesVariableNames.Enabled, "True");
        ctx.Variables.Set(SubstituteInFilesVariableNames.TargetFiles, "*.txt");
        ctx.Variables.Set("X", "1");

        await new SubstituteInFilesStep().ExecuteAsync(ctx, CancellationToken.None);

        var outcome = ctx.StepOutcomes.ShouldHaveSingleItem();
        outcome.StepName.ShouldBe(SubstituteInFilesStep.StepName);
        outcome.Status.ShouldBe(StepStatus.Succeeded);
        outcome.Metrics["FilesProcessed"].ShouldBe(1);
        outcome.Metrics["FilesSkipped"].ShouldBe(0);
        outcome.Metrics["FilesWithUnresolvedTokens"].ShouldBe(0);
    }

    [Fact]
    public async Task ConfigurationTransformsStep_EmitsOutcome_WithAppliedAndFailedCounts()
    {
        // Stage a base + matching env transform so 1 apply lands.
        File.WriteAllText(Path.Combine(_workDir, "web.config"),
            "<?xml version=\"1.0\"?><configuration><appSettings><add key=\"x\" value=\"old\" /></appSettings></configuration>");
        File.WriteAllText(Path.Combine(_workDir, "web.Production.config"),
            "<?xml version=\"1.0\"?><configuration xmlns:xdt=\"http://schemas.microsoft.com/XML-Document-Transform\">" +
            "<appSettings><add key=\"x\" value=\"new\" xdt:Transform=\"SetAttributes\" xdt:Locator=\"Match(key)\" /></appSettings></configuration>");

        var ctx = BuildContext();
        ctx.Variables!.Set(ConfigurationTransformsVariableNames.Enabled, "True");
        ctx.Variables.Set(ConfigurationTransformsVariableNames.EnvironmentName, "Production");

        await new ConfigurationTransformsStep().ExecuteAsync(ctx, CancellationToken.None);

        var outcome = ctx.StepOutcomes.ShouldHaveSingleItem();
        outcome.StepName.ShouldBe(ConfigurationTransformsStep.StepName);
        outcome.Metrics["TransformsApplied"].ShouldBe(1);
        outcome.Metrics["TransformsFailed"].ShouldBe(0);
    }

    [Fact]
    public async Task StructuredConfigStep_EmitsOutcome_WithReplacementCount()
    {
        File.WriteAllText(Path.Combine(_workDir, "appsettings.json"), """{"K":"old"}""");

        var ctx = BuildContext();
        ctx.Variables!.Set(StructuredConfigVariableNames.Enabled, "True");
        ctx.Variables.Set(StructuredConfigVariableNames.Targets, "appsettings.json");
        ctx.Variables.Set("K", "new");

        await new StructuredConfigVariablesStep().ExecuteAsync(ctx, CancellationToken.None);

        var outcome = ctx.StepOutcomes.ShouldHaveSingleItem();
        outcome.StepName.ShouldBe(StructuredConfigVariablesStep.StepName);
        outcome.Metrics["FilesProcessed"].ShouldBe(1);
        outcome.Metrics["LeavesReplaced"].ShouldBe(1);
    }

    [Fact]
    public async Task StructuredConfigStep_EmptyTargets_EmitsSkippedOutcome_NotSuccess()
    {
        // Empty Targets short-circuits in IsEnabled-passes case. MUST emit
        // a Skipped outcome (not silently nothing) so UI shows "step ran,
        // but skipped because operator didn't list any targets".
        var ctx = BuildContext();
        ctx.Variables!.Set(StructuredConfigVariableNames.Enabled, "True");
        ctx.Variables.Set(StructuredConfigVariableNames.Targets, "");

        await new StructuredConfigVariablesStep().ExecuteAsync(ctx, CancellationToken.None);

        var outcome = ctx.StepOutcomes.ShouldHaveSingleItem();
        outcome.Status.ShouldBe(StepStatus.Skipped);
        outcome.Message.ShouldBe("Targets glob is empty");
    }

    [Fact]
    public async Task ConventionScriptStep_EmitsOutcome_WhenHookRuns()
    {
        File.WriteAllText(Path.Combine(_workDir, "PreDeploy.sh"), "echo hi");

        var ctx = BuildContext();
        var stubEngine = new Conventions.StubScriptEngine(exitCode: 0);

        await new ConventionScriptStep(ConventionScriptNames.PreDeploy, stubEngine).ExecuteAsync(ctx, CancellationToken.None);

        var outcome = ctx.StepOutcomes.ShouldHaveSingleItem();
        outcome.StepName.ShouldBe(ConventionScriptNames.PreDeploy);
        outcome.Status.ShouldBe(StepStatus.Succeeded);
        outcome.Metrics["ExitCode"].ShouldBe(0);
    }

    [Fact]
    public async Task DeployFailedConventionStep_EmitsOutcome_WhenHookRunsCleanly()
    {
        // On a failure path, DeployFailed runs and emits an outcome.
        File.WriteAllText(Path.Combine(_workDir, "DeployFailed.sh"), "echo cleanup");

        var ctx = BuildContext();
        ctx.ExecutionFailed = true;
        var stubEngine = new Conventions.StubScriptEngine(exitCode: 0);

        await new DeployFailedConventionStep(stubEngine).ExecuteAsync(ctx, CancellationToken.None);

        var outcome = ctx.StepOutcomes.ShouldHaveSingleItem();
        outcome.StepName.ShouldBe(ConventionScriptNames.DeployFailed);
        outcome.Status.ShouldBe(StepStatus.Succeeded);
        outcome.Metrics["ExitCode"].ShouldBe(0);
    }

    [Fact]
    public async Task DeployFailedConventionStep_HookItselfFails_EmitsFailedOutcome_StillNoThrow()
    {
        File.WriteAllText(Path.Combine(_workDir, "DeployFailed.sh"), "exit 5");

        var ctx = BuildContext();
        ctx.ExecutionFailed = true;
        var stubEngine = new Conventions.StubScriptEngine(exitCode: 5);

        await Should.NotThrowAsync(() =>
            new DeployFailedConventionStep(stubEngine).ExecuteAsync(ctx, CancellationToken.None));

        var outcome = ctx.StepOutcomes.ShouldHaveSingleItem();
        outcome.Status.ShouldBe(StepStatus.Failed,
            customMessage: "DeployFailed hook failure MUST emit Failed status in the outcome — even though the step doesn't re-throw, the structured surface should record the failure for analytics.");
        outcome.Metrics["ExitCode"].ShouldBe(5);
    }

    [Fact]
    public async Task ContextStepOutcomesList_StartsEmpty_PreservesAppendOrder()
    {
        // Defensive: pipeline state pinning. New context = empty list.
        // Multiple step appends preserve insertion order.
        var ctx = BuildContext();
        ctx.StepOutcomes.ShouldBeEmpty();

        ctx.StepOutcomes.Add(StepOutcome.Success("A"));
        ctx.StepOutcomes.Add(StepOutcome.Skipped("B", "x"));
        ctx.StepOutcomes.Add(StepOutcome.Success("C"));

        ctx.StepOutcomes.Select(o => o.StepName).ShouldBe(new[] { "A", "B", "C" });
    }

    private RunScriptCommandContext BuildContext()
    {
        return new RunScriptCommandContext
        {
            ScriptPath = Path.Combine(_workDir, "script.sh"),
            VariablesPath = Path.Combine(_workDir, "variables.json"),
            WorkingDirectory = _workDir,
            Variables = new VariableSet()
        };
    }
}

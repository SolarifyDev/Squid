using System.Text;
using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Helpers;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// E2E-1 — comprehensive coverage of the <c>Squid.HelmChartUpgrade</c> action
/// across both <c>KubernetesApi</c> and <c>KubernetesAgent</c> communication
/// styles.
///
/// <para><b>Why this exists</b>: pre-this-PR, the only Helm E2E coverage was
/// (a) <see cref="Api.HelmUpgradeE2ETests"/> — three tests all marked
/// <c>[Fact(Skip = "Requires helm CLI")]</c> so they never run in CI; plus
/// (b) <c>KubernetesVariableSubstitutionE2ETests.HelmUpgrade_WithVariableInAdditionalArgs_*</c>
/// — a single variable-substitution check. <see cref="HelmUpgradeActionHandler"/>
/// + <see cref="Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering.HelmUpgradeScriptBuilder"/>
/// have substantial surface (release / chart / repo / values files / inline
/// values / wait / timeout / reset / custom executable / additional args)
/// that no test exercised. Architecture audit flagged this as the most
/// critical 🔴 gap before more Helm work landed.</para>
///
/// <para><b>Pattern</b>: <c>DeploymentPipelineFixture</c> (Pattern 2 in
/// CLAUDE.md). Tests run the full pipeline end-to-end with
/// <see cref="CapturingExecutionStrategy"/> standing in for both K8s API
/// kubectl and the K8s Agent's Halibut RPC. The pipeline produces a
/// <c>ScriptExecutionRequest</c> per target whose <c>ScriptBody</c> + <c>Files</c>
/// we inspect. We do NOT execute helm against a real cluster — that's the
/// existing CLI-skipped test's job. This pattern lets us verify the FULL
/// variable-substitution + intent-emission + script-rendering chain without
/// requiring helm on the CI runner.</para>
///
/// <para><b>Why Theory(KubernetesApi, KubernetesAgent)</b>: both transports
/// share the same <c>HelmUpgradeScriptBuilder</c> output (per the doc-comment
/// on that class). Running the same assertions against both styles pins the
/// shared-impl invariant — if a future refactor splits the builder per
/// transport, these tests catch the divergence.</para>
///
/// <para><b>Security pin — P0-Phase10.2</b>: inline key=value pairs MUST be
/// projected to a generated <c>inline-values.yaml</c> file rather than emitted
/// as <c>--set key=value</c> argv. Argv leaks into <c>ps</c>, <c>/proc/&lt;pid&gt;/cmdline</c>,
/// kubelet logs, and audit logs — unacceptable for sensitive values (DB
/// password, API key, TLS private key). <see cref="HelmUpgrade_InlineKeyValues_GeneratedAsYamlFile_NotSetArgv"/>
/// is the regression test for that hardening.</para>
/// </summary>
[Collection("KindCluster")]
[Trait("Category", "E2E")]
[Trait("Tier", "Contract")]
public class HelmChartUpgradeE2ETests
    : IClassFixture<DeploymentPipelineFixture<HelmChartUpgradeE2ETests>>
{
    private readonly KindClusterFixture _cluster;
    private readonly DeploymentPipelineFixture<HelmChartUpgradeE2ETests> _fixture;

    public HelmChartUpgradeE2ETests(
        KindClusterFixture cluster,
        DeploymentPipelineFixture<HelmChartUpgradeE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    // ── 1. Basic chart → "helm upgrade --install <release> <chart>" ─────────

    [Theory]
    [InlineData("KubernetesApi")]
    [InlineData("KubernetesAgent")]
    public async Task HelmUpgrade_BasicChart_GeneratesUpgradeCommand(string communicationStyle)
    {
        var releaseName = $"e2e-basic-{Guid.NewGuid().ToString("N")[..6]}";

        var serverTaskId = await SeedHelmAsync(communicationStyle, properties: new()
        {
            ["Squid.Action.Helm.ReleaseName"] = releaseName,
            ["Squid.Action.Helm.ChartPath"] = "bitnami/nginx",
            ["Squid.Action.Script.Syntax"] = "Bash"
        });

        await ExecutePipelineAsync(serverTaskId);

        await AssertTaskSuccessAsync();
        var script = SingleCapturedScriptBody();

        script.ShouldContain("helm",
            customMessage: "Generated Helm script must invoke helm; if missing, the renderer didn't run for this style.");
        script.ShouldContain("upgrade --install",
            customMessage: "Helm action MUST emit `upgrade --install` (not `install` or `upgrade`) — operators rely on idempotent re-deploys.");
        script.ShouldContain($"\"{releaseName}\"",
            customMessage: "Release name MUST appear quoted in the script. Verify HelmUpgradeIntent.ReleaseName flows to the renderer.");
        script.ShouldContain("\"bitnami/nginx\"",
            customMessage: "Chart reference MUST appear quoted; verify HelmUpgradeIntent.ChartReference is populated from the ChartPath property.");
    }

    // ── 2. Namespace flag ─────────────────────────────────────────────────

    [Theory]
    [InlineData("KubernetesApi")]
    [InlineData("KubernetesAgent")]
    public async Task HelmUpgrade_WithNamespace_AppliesNamespaceFlag(string communicationStyle)
    {
        var ns = $"helm-ns-{Guid.NewGuid().ToString("N")[..6]}";

        var serverTaskId = await SeedHelmAsync(communicationStyle, properties: new()
        {
            ["Squid.Action.Helm.ReleaseName"] = "myrelease",
            ["Squid.Action.Helm.ChartPath"] = "stable/redis",
            ["Squid.Action.Kubernetes.Namespace"] = ns,
            ["Squid.Action.Script.Syntax"] = "Bash"
        });

        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync();

        var script = SingleCapturedScriptBody();
        script.ShouldContain($"--namespace \"{ns}\"",
            customMessage: "Namespace MUST flow from action property to --namespace flag. " +
                          "Operators set this per-environment to isolate tenants; missing it = wrong-namespace deploy.");
    }

    // ── 3. Inline YAML values → values file staged + --values flag ─────────

    [Theory]
    [InlineData("KubernetesApi")]
    [InlineData("KubernetesAgent")]
    public async Task HelmUpgrade_InlineYamlValues_StagesFileAndReferencesIt(string communicationStyle)
    {
        const string yamlValues = """
            replicaCount: 3
            image:
              tag: v2.5.0
            service:
              type: ClusterIP
            """;

        var serverTaskId = await SeedHelmAsync(communicationStyle, properties: new()
        {
            ["Squid.Action.Helm.ReleaseName"] = "vals",
            ["Squid.Action.Helm.ChartPath"] = "./chart",
            ["Squid.Action.Helm.YamlValues"] = yamlValues,
            ["Squid.Action.Script.Syntax"] = "Bash"
        });

        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync();

        var request = ExecutionCapture.CapturedRequests.ShouldHaveSingleItem();

        // The values file is staged on the request as a DeploymentFile
        request.DeploymentFiles.ShouldContain(f => f.RelativePath == "rawYamlValues.yaml",
            customMessage: "Inline YAML values MUST be staged as `rawYamlValues.yaml` in the request's DeploymentFiles. " +
                          "Without the file, --values references a path that doesn't exist on disk.");

        var valuesFile = request.DeploymentFiles.Single(f => f.RelativePath == "rawYamlValues.yaml");
        var content = Encoding.UTF8.GetString(valuesFile.Content);
        content.ShouldContain("replicaCount: 3");
        content.ShouldContain("v2.5.0");

        request.ScriptBody.ShouldContain("--values \"./rawYamlValues.yaml\"",
            customMessage: "Script MUST reference the staged values file via --values; otherwise helm runs without the values.");
    }

    // ── 4. Inline KeyValues → generated inline-values.yaml (P0-Phase10.2 security pin) ──

    [Theory]
    [InlineData("KubernetesApi")]
    [InlineData("KubernetesAgent")]
    public async Task HelmUpgrade_InlineKeyValues_GeneratedAsYamlFile_NotSetArgv(string communicationStyle)
    {
        // Two key-value pairs — one of them is a "secret" simulant (DB password)
        // that absolutely MUST NOT land in argv per P0-Phase10.2.
        var keyValuesJson = """{"image.tag":"v3","database.password":"super-secret-do-not-leak"}""";

        var serverTaskId = await SeedHelmAsync(communicationStyle, properties: new()
        {
            ["Squid.Action.Helm.ReleaseName"] = "secrets-test",
            ["Squid.Action.Helm.ChartPath"] = "./chart",
            ["Squid.Action.Helm.KeyValues"] = keyValuesJson,
            ["Squid.Action.Script.Syntax"] = "Bash"
        });

        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync();

        var request = ExecutionCapture.CapturedRequests.ShouldHaveSingleItem();

        // Pin: a generated inline-values.yaml MUST be staged.
        request.DeploymentFiles.ShouldContain(f => f.RelativePath == "inline-values.yaml",
            customMessage: "P0-Phase10.2 hardening: inline KeyValues MUST be projected to a generated " +
                          "`inline-values.yaml` file rather than emitted as `--set k=v` argv. " +
                          "If this fails, secrets like database.password are leaking to ps / kubelet logs / audit.");

        var inlineValues = request.DeploymentFiles.Single(f => f.RelativePath == "inline-values.yaml");
        var content = Encoding.UTF8.GetString(inlineValues.Content);
        content.ShouldContain("super-secret-do-not-leak",
            customMessage: "Generated YAML MUST carry the actual secret value (encrypted at rest by P0-3 in checkpoint, but plaintext to helm).");

        // Pin: argv MUST NOT contain `--set` for inline values.
        request.ScriptBody.ShouldNotContain("--set ",
            customMessage: "P0-Phase10.2 SECURITY REGRESSION: inline values landed in argv. " +
                          "Verify HelmUpgradeScriptBuilder.AppendBashInlineValuesAsFile is still called and that " +
                          "TryGenerateInlineValuesFile is not bypassed.");

        // Pin: the inline-values.yaml is referenced via --values.
        request.ScriptBody.ShouldContain("--values \"./inline-values.yaml\"",
            customMessage: "Generated inline-values.yaml MUST be referenced via --values; otherwise helm " +
                          "ignores the values entirely and the deploy uses chart defaults.");
    }

    // ── 5. Dotted keys produce nested YAML ────────────────────────────────

    [Fact]
    public async Task HelmUpgrade_DottedKeyValues_ProduceNestedYamlTree()
    {
        // Helm's --set semantics: `database.host=db.example.com` produces
        // YAML { database: { host: db.example.com } }. The HelmUpgradeScriptBuilder
        // explicitly mirrors that — verify two keys under the same parent
        // collapse to one nested block (not duplicate top-level keys).
        var keyValuesJson = """{"database.host":"db.example.com","database.port":"5432","app.name":"foo"}""";

        var serverTaskId = await SeedHelmAsync("KubernetesApi", properties: new()
        {
            ["Squid.Action.Helm.ReleaseName"] = "nested",
            ["Squid.Action.Helm.ChartPath"] = "./chart",
            ["Squid.Action.Helm.KeyValues"] = keyValuesJson,
            ["Squid.Action.Script.Syntax"] = "Bash"
        });

        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync();

        var request = ExecutionCapture.CapturedRequests.ShouldHaveSingleItem();
        var inline = request.DeploymentFiles.Single(f => f.RelativePath == "inline-values.yaml");
        var content = Encoding.UTF8.GetString(inline.Content);

        // Should produce a single `database:` block containing both host + port,
        // NOT two separate `database.host:` / `database.port:` top-level keys.
        // The expected nested form (sorted alphabetically by key):
        //   app:
        //     name: 'foo'
        //   database:
        //     host: 'db.example.com'
        //     port: '5432'
        content.ShouldContain("database:");
        content.ShouldContain("  host: 'db.example.com'",
            customMessage: "host MUST be nested 2-space-indented under database — Helm --set dot-notation semantics.");
        content.ShouldContain("  port: '5432'");
        content.ShouldContain("app:");
        content.ShouldContain("  name: 'foo'");

        content.ShouldNotContain("database.host:",
            customMessage: "Dotted keys MUST collapse into a nested tree — leaving `database.host:` as a top-level key " +
                          "means Helm consumes a malformed values.yaml.");
    }

    // ── 6. Variable substitution ───────────────────────────────────────────

    [Theory]
    [InlineData("KubernetesApi")]
    [InlineData("KubernetesAgent")]
    public async Task HelmUpgrade_VariableSubstitution_ResolvesPlaceholdersInScript(string communicationStyle)
    {
        var ns = $"helm-vars-{Guid.NewGuid().ToString("N")[..6]}";

        var serverTaskId = await SeedHelmAsync(communicationStyle, properties: new()
        {
            // Use #{} placeholders for every action property that supports it
            ["Squid.Action.Helm.ReleaseName"] = "#{ReleaseName}",
            ["Squid.Action.Helm.ChartPath"] = "stable/redis",
            ["Squid.Action.Kubernetes.Namespace"] = "#{Namespace}",
            ["Squid.Action.Helm.AdditionalArgs"] = "--description \"#{AppEnv}\"",
            ["Squid.Action.Script.Syntax"] = "Bash"
        }, extraVariables: new[]
        {
            ("ReleaseName", "myapp-prod"),
            ("Namespace", ns)
        });

        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync();

        var script = SingleCapturedScriptBody();

        script.ShouldNotContain("#{",
            customMessage: "Variable substitution failed — placeholders leaked into the rendered script. " +
                          "If you see `#{ReleaseName}` in the script, the variable expander didn't run on " +
                          "Helm action properties. Check that ExpandActionProperties wires all property names.");

        script.ShouldContain("\"myapp-prod\"");
        script.ShouldContain($"--namespace \"{ns}\"");
        script.ShouldContain("--description \"e2e-test\"");   // AppEnv default in seeder
    }

    // ── 7. Wait + WaitForJobs + Timeout flags ─────────────────────────────

    [Theory]
    [InlineData("KubernetesApi")]
    [InlineData("KubernetesAgent")]
    public async Task HelmUpgrade_WaitAndTimeoutFlags_AppendedCorrectly(string communicationStyle)
    {
        var serverTaskId = await SeedHelmAsync(communicationStyle, properties: new()
        {
            ["Squid.Action.Helm.ReleaseName"] = "waiter",
            ["Squid.Action.Helm.ChartPath"] = "./chart",
            ["Squid.Action.Helm.Wait"] = "True",
            ["Squid.Action.Helm.WaitForJobs"] = "True",
            ["Squid.Action.Helm.Timeout"] = "10m",
            ["Squid.Action.Script.Syntax"] = "Bash"
        });

        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync();

        var script = SingleCapturedScriptBody();

        script.ShouldContain(" --wait",
            customMessage: "--wait MUST be present when Squid.Action.Helm.Wait=True");
        script.ShouldContain(" --wait-for-jobs",
            customMessage: "--wait-for-jobs MUST be present when Squid.Action.Helm.WaitForJobs=True");
        script.ShouldContain("--timeout \"10m\"",
            customMessage: "Timeout MUST flow from action property to --timeout flag. " +
                          "Operators set this for slow-starting workloads; missing it falls back to helm default (5m).");
    }

    // ── 8. ResetValues default = true (no property → flag present) ─────────

    [Fact]
    public async Task HelmUpgrade_ResetValuesDefaultTrue_FlagPresent()
    {
        // Doc-comment on HelmUpgradeIntent.ResetValues default = true.
        // Operators rely on this default; flipping it would change semantics
        // for everyone who hasn't explicitly set the property.
        var serverTaskId = await SeedHelmAsync("KubernetesApi", properties: new()
        {
            ["Squid.Action.Helm.ReleaseName"] = "default-reset",
            ["Squid.Action.Helm.ChartPath"] = "./chart",
            ["Squid.Action.Script.Syntax"] = "Bash"
            // NO Squid.Action.Helm.ResetValues property → default true
        });

        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync();

        var script = SingleCapturedScriptBody();
        script.ShouldContain("--reset-values",
            customMessage: "ResetValues default MUST be true — operators not setting the property expect " +
                          "their values to override the chart defaults. Changing default to false would " +
                          "silently break every deploy that didn't explicitly set the property.");
    }

    [Fact]
    public async Task HelmUpgrade_ResetValuesExplicitFalse_FlagAbsent()
    {
        var serverTaskId = await SeedHelmAsync("KubernetesApi", properties: new()
        {
            ["Squid.Action.Helm.ReleaseName"] = "no-reset",
            ["Squid.Action.Helm.ChartPath"] = "./chart",
            ["Squid.Action.Helm.ResetValues"] = "False",
            ["Squid.Action.Script.Syntax"] = "Bash"
        });

        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync();

        var script = SingleCapturedScriptBody();
        script.ShouldNotContain("--reset-values",
            customMessage: "Operators setting ResetValues=False expect helm to PRESERVE existing release values; " +
                          "if --reset-values appears, those values are discarded.");
    }

    // ── 9. AdditionalArgs appended at end ──────────────────────────────────

    [Theory]
    [InlineData("KubernetesApi")]
    [InlineData("KubernetesAgent")]
    public async Task HelmUpgrade_AdditionalArgs_AppendedAtEnd(string communicationStyle)
    {
        const string extras = "--atomic --create-namespace --description \"deploy-2026\"";

        var serverTaskId = await SeedHelmAsync(communicationStyle, properties: new()
        {
            ["Squid.Action.Helm.ReleaseName"] = "extra-args",
            ["Squid.Action.Helm.ChartPath"] = "./chart",
            ["Squid.Action.Helm.AdditionalArgs"] = extras,
            ["Squid.Action.Script.Syntax"] = "Bash"
        });

        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync();

        var script = SingleCapturedScriptBody();
        script.ShouldContain(extras,
            customMessage: "AdditionalArgs MUST be appended verbatim at end of helm command line — operators " +
                          "use this for flags Squid doesn't model (--atomic, --create-namespace, --post-renderer, etc.).");
    }

    // ── 10. CustomHelmExecutable ──────────────────────────────────────────

    [Fact]
    public async Task HelmUpgrade_CustomHelmExecutable_UsedInsteadOfDefaultHelm()
    {
        // Operators with custom helm builds (helm3-rc, vendored binary,
        // version-pinned helm at /opt/helm-3.13.1/helm) need the script
        // to invoke their executable, not the default `helm` from PATH.
        var serverTaskId = await SeedHelmAsync("KubernetesApi", properties: new()
        {
            ["Squid.Action.Helm.ReleaseName"] = "custom-bin",
            ["Squid.Action.Helm.ChartPath"] = "./chart",
            ["Squid.Action.Helm.CustomHelmExecutable"] = "/opt/helm3/bin/helm",
            ["Squid.Action.Script.Syntax"] = "Bash"
        });

        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync();

        var script = SingleCapturedScriptBody();
        script.ShouldContain("\"/opt/helm3/bin/helm\" upgrade --install",
            customMessage: "Custom helm executable MUST be used in place of default `helm`. If this fails, " +
                          "operators with vendored helm binaries silently fall back to PATH lookup.");
        script.ShouldNotContain("\"helm\" upgrade",
            customMessage: "Default `helm` MUST NOT appear when CustomHelmExecutable is set — that defeats the override.");
    }

    // ── 11. PowerShell syntax ─────────────────────────────────────────────

    [Fact]
    public async Task HelmUpgrade_PowerShellSyntax_GeneratesPowerShellScript()
    {
        // PS variant uses `$helmArgs = @(...)` array + `& $helmExe @helmArgs`
        // pattern (HelmUpgradeScriptBuilder.BuildPowerShell). Operators on
        // Windows or running PowerShell-only Tentacles need this path.
        var serverTaskId = await SeedHelmAsync("KubernetesApi", properties: new()
        {
            ["Squid.Action.Helm.ReleaseName"] = "ps-release",
            ["Squid.Action.Helm.ChartPath"] = "./chart",
            ["Squid.Action.Script.Syntax"] = "PowerShell"
        });

        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync();

        var script = SingleCapturedScriptBody();

        script.ShouldContain("$ErrorActionPreference = \"Stop\"",
            customMessage: "PowerShell variant MUST set ErrorActionPreference at the top — without it, " +
                          "non-zero helm exit doesn't fail the script and the deploy reports success.");
        script.ShouldContain("$helmArgs",
            customMessage: "PS variant builds args via $helmArgs array — if missing, the renderer didn't dispatch on Syntax.");
        script.ShouldContain("if ($LASTEXITCODE -ne 0)",
            customMessage: "PS variant MUST check $LASTEXITCODE and throw on non-zero — without it, helm errors are swallowed.");
    }

    // ── 12. Per-target dispatch (multi-target) ────────────────────────────

    [Fact]
    public async Task HelmUpgrade_MultipleTargets_EachReceivesIndependentRequest()
    {
        // When a step has 3 K8s targets, the pipeline dispatches the action
        // to each independently. Each target gets its own ScriptExecutionRequest.
        // Verify: 3 captured requests, each with the correct machine name.
        var serverTaskId = await SeedHelmWithMultipleTargetsAsync(targetCount: 3);

        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync();

        ExecutionCapture.CapturedRequests.Count.ShouldBe(3,
            customMessage: "3 targets MUST produce 3 captured requests. " +
                          "If fewer, the pipeline lost dispatches. If more, double-dispatch (idempotence bug).");

        var machineNames = ExecutionCapture.CapturedRequests
            .Select(r => r.Machine?.Name)
            .Where(n => n != null)
            .ToList();

        machineNames.Count.ShouldBe(3);
        machineNames.Distinct().Count().ShouldBe(3,
            customMessage: "Each captured request MUST carry the unique target machine — same machine appearing twice " +
                          "indicates per-target dispatch is broken.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task ExecutePipelineAsync(int serverTaskId)
    {
        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task AssertTaskSuccessAsync()
    {
        // Same pattern as KubernetesContainersDeployE2ETests.AssertTaskSuccessAsync —
        // pull all tasks, find the one matching our seeded ID, assert state.
        await _fixture.Run<IServerTaskDataProvider>(async taskDataProvider =>
        {
            var tasks = await taskDataProvider.GetAllServerTasksAsync(CancellationToken.None).ConfigureAwait(false);
            var task = tasks.SingleOrDefault(t => t.Id == _lastSeededTaskId);
            task.ShouldNotBeNull(
                customMessage: $"Could not find seeded ServerTask {_lastSeededTaskId} in DB — was the seeder cancelled mid-flight?");
            task.State.ShouldBe(TaskState.Success,
                customMessage: $"Deployment task {_lastSeededTaskId} ended in state {task.State} " +
                              "(expected Success). Check captured logs via /tmp/squid-test-logs or query " +
                              "DeploymentExecutionCheckpoint table for the partial state.");
        }).ConfigureAwait(false);
    }

    private string SingleCapturedScriptBody()
    {
        var request = ExecutionCapture.CapturedRequests.ShouldHaveSingleItem(
            customMessage: $"Expected exactly 1 captured request; got {ExecutionCapture.CapturedRequests.Count}. " +
                          "If 0: the pipeline didn't dispatch (check action handler + capability validation). " +
                          "If >1: per-target dispatch is over-firing (multi-target test should be used).");
        return request.ScriptBody;
    }

    /// <summary>
    /// Seeds a single-target Helm deploy with the given action properties.
    /// </summary>
    private int _lastSeededTaskId;

    private async Task<int> SeedHelmAsync(
        string communicationStyle,
        Dictionary<string, string> properties,
        IEnumerable<(string Name, string Value)> extraVariables = null)
    {
        ExecutionCapture.Clear();

        var taskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            taskId = await SeedHelmTestDataAsync(repository, unitOfWork, communicationStyle, properties, extraVariables);
        }).ConfigureAwait(false);

        _lastSeededTaskId = taskId;
        return taskId;
    }

    private async Task<int> SeedHelmWithMultipleTargetsAsync(int targetCount)
    {
        ExecutionCapture.Clear();

        var taskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            taskId = await SeedMultiTargetHelmTestDataAsync(repository, unitOfWork, targetCount);
        }).ConfigureAwait(false);

        _lastSeededTaskId = taskId;
        return taskId;
    }

    private static async Task<int> SeedHelmTestDataAsync(
        IRepository repository,
        IUnitOfWork unitOfWork,
        string communicationStyle,
        Dictionary<string, string> properties,
        IEnumerable<(string Name, string Value)> extraVariables)
    {
        var builder = new TestDataBuilder(repository, unitOfWork);

        var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);

        var defaultVars = new List<(string Name, string Value, VariableType Type, bool Sensitive)>
        {
            ("AppEnv", "e2e-test", VariableType.String, false)
        };
        if (extraVariables != null)
            foreach (var v in extraVariables)
                defaultVars.Add((v.Name, v.Value, VariableType.String, false));

        await builder.CreateVariablesAsync(variableSet.Id, defaultVars.ToArray()).ConfigureAwait(false);

        var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
        await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

        var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
        await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

        var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Helm Deploy").ConfigureAwait(false);
        await builder.CreateStepPropertiesAsync(step.Id, ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

        var action = await builder.CreateDeploymentActionAsync(
            step.Id, 1, "Helm Upgrade", actionType: "Squid.HelmChartUpgrade").ConfigureAwait(false);

        await builder.CreateActionMachineRolesAsync(action.Id, "k8s").ConfigureAwait(false);
        await builder.CreateActionPropertiesAsync(action.Id,
            properties.Select(kvp => (kvp.Key, kvp.Value)).ToArray()).ConfigureAwait(false);

        return await CreateInfrastructureAsync(builder, repository, unitOfWork, project, communicationStyle, targetCount: 1).ConfigureAwait(false);
    }

    private static async Task<int> SeedMultiTargetHelmTestDataAsync(
        IRepository repository, IUnitOfWork unitOfWork, int targetCount)
    {
        var builder = new TestDataBuilder(repository, unitOfWork);

        var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
        await builder.CreateVariablesAsync(variableSet.Id,
            ("AppEnv", "e2e-test", VariableType.String, false)).ConfigureAwait(false);

        var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
        await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

        var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
        await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

        var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Helm Multi").ConfigureAwait(false);
        await builder.CreateStepPropertiesAsync(step.Id, ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

        var action = await builder.CreateDeploymentActionAsync(
            step.Id, 1, "Helm Upgrade Multi", actionType: "Squid.HelmChartUpgrade").ConfigureAwait(false);

        await builder.CreateActionMachineRolesAsync(action.Id, "k8s").ConfigureAwait(false);
        await builder.CreateActionPropertiesAsync(action.Id,
            ("Squid.Action.Helm.ReleaseName", "multi-target"),
            ("Squid.Action.Helm.ChartPath", "./chart"),
            ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

        return await CreateInfrastructureAsync(builder, repository, unitOfWork, project, "KubernetesApi", targetCount).ConfigureAwait(false);
    }

    private static async Task<int> CreateInfrastructureAsync(
        TestDataBuilder builder, IRepository repository, IUnitOfWork unitOfWork,
        Project project, string communicationStyle, int targetCount,
        CancellationToken ct = default)
    {
        var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
        var environment = await builder.CreateEnvironmentAsync("E2E Helm Environment").ConfigureAwait(false);

        for (var i = 0; i < targetCount; i++)
        {
            var machine = communicationStyle == "KubernetesAgent"
                ? CreateAgentMachine(environment, $"target-{i}")
                : CreateApiMachine(environment, $"target-{i}");

            await repository.InsertAsync(machine, ct).ConfigureAwait(false);
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        // KubernetesApi needs an account; KubernetesAgent doesn't.
        if (communicationStyle != "KubernetesAgent")
        {
            var account = new DeploymentAccount
            {
                SpaceId = 1,
                Name = "E2E Helm Account",
                Slug = "e2e-helm-account",
                AccountType = AccountType.Token,
                Credentials = DeploymentAccountCredentialsConverter.Serialize(
                    new TokenCredentials { Token = "e2e-helm-token" })
            };
            await repository.InsertAsync(account, ct).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);
        var deployment = new Deployment
        {
            Name = "E2E Helm Deployment",
            SpaceId = 1, ChannelId = channel.Id, ProjectId = project.Id,
            ReleaseId = release.Id, EnvironmentId = environment.Id,
            DeployedBy = 1, CreatedDate = DateTimeOffset.UtcNow, Json = string.Empty
        };
        await repository.InsertAsync(deployment, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        var serverTask = new ServerTask
        {
            Name = "E2E Helm Task", Description = "E2E Helm test task",
            QueueTime = DateTimeOffset.UtcNow, State = TaskState.Pending,
            ServerTaskType = "Deploy", ProjectId = project.Id, EnvironmentId = environment.Id,
            SpaceId = 1, LastModifiedDate = DateTimeOffset.UtcNow,
            BusinessProcessState = "Queued", StateOrder = 1, Weight = 1, BatchId = 0,
            JSON = string.Empty, HasWarningsOrErrors = false,
            ServerNodeId = Guid.NewGuid(), DurationSeconds = 0, DataVersion = Array.Empty<byte>()
        };
        await repository.InsertAsync(serverTask, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        deployment.TaskId = serverTask.Id;
        await repository.UpdateAsync(deployment, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        return serverTask.Id;
    }

    private static Machine CreateApiMachine(Environment environment, string nameSuffix)
    {
        var endpointJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = "https://localhost:6443",
            SkipTlsVerification = "True",
            Namespace = "default",
            ResourceReferences = new[]
            {
                new { Type = (int)EndpointResourceType.AuthenticationAccount, ResourceId = 1 }
            }
        });

        return new Machine
        {
            Name = $"E2E Helm API {nameSuffix}",
            IsDisabled = false,
            Roles = DeploymentTargetFinder.SerializeRoles(new[] { "k8s" }),
            EnvironmentIds = DeploymentTargetFinder.SerializeIds(new[] { environment.Id }),
            Endpoint = endpointJson,
            SpaceId = 1,
            Slug = $"e2e-helm-api-{nameSuffix}-{Guid.NewGuid().ToString("N")[..6]}"
        };
    }

    private static Machine CreateAgentMachine(Environment environment, string nameSuffix)
    {
        var subscriptionId = Guid.NewGuid().ToString("N");
        var endpointJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesAgent",
            SubscriptionId = subscriptionId,
            Thumbprint = "E2E-HELM-THUMBPRINT",
            Namespace = "default"
        });

        return new Machine
        {
            Name = $"E2E Helm Agent {nameSuffix}",
            IsDisabled = false,
            Roles = DeploymentTargetFinder.SerializeRoles(new[] { "k8s" }),
            EnvironmentIds = DeploymentTargetFinder.SerializeIds(new[] { environment.Id }),
            Endpoint = endpointJson,
            SpaceId = 1,
            Slug = $"e2e-helm-agent-{nameSuffix}-{subscriptionId[..6]}"
        };
    }
}

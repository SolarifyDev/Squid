using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Deployments.Kubernetes.Agent;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// End-to-end coverage for the <c>##squid[setVariable]</c> cross-step output-variable
/// chain on a REAL Kind cluster via REAL Halibut polling.
///
/// <para><b>Production gap closed</b>: output variables are the canonical mechanism
/// by which Squid steps pass data to each other (a probe step emits the resolved
/// namespace / image tag / connection string, a downstream step consumes it via
/// <c>#{Squid.Action.&lt;StepName&gt;.&lt;VarName&gt;}</c>). The parser (line capture +
/// service-message decoding), the merger (collision handling + sensitive masking),
/// and the variable-substitution layer all have isolated unit tests. NO test
/// proves the full chain works end-to-end through Halibut polling RPC and the
/// Effective-Variables build for step 2. A regression in any seam — script execution
/// not piping log lines through the parser, parser dropping the service-message
/// format, merger storing the value under a different key, or the substitution layer
/// not re-resolving for the second step — would silently leave step 2 running with
/// the literal <c>#{…}</c> token still in the script body, and pass component tests.</para>
///
/// <para><b>The 5-link chain that this test proves end-to-end</b>:
/// <list type="number">
///   <item>Step 1's script body emits an <c>##squid[setVariable name='X' value='Y']</c>
///         service message via <c>echo</c> on the agent</item>
///   <item>Halibut polling RPC streams the log line back to the server</item>
///   <item>The output-variable parser (in <c>DeploymentTaskExecutor.Script.cs</c>)
///         decodes the service message and stores <c>"Squid.Action.&lt;StepName&gt;.X"</c>
///         + unqualified <c>"X"</c> in <c>_ctx.Variables</c></item>
///   <item>For step 2, <c>BuildEffectiveVariables</c> includes these output variables</item>
///   <item>The script-body expansion replaces <c>#{Squid.Action.&lt;StepName&gt;.X}</c>
///         with the resolved value, then the agent runs the resolved script and we
///         can grep the cluster-side output for the value</item>
/// </list></para>
///
/// <para><b>Why <c>kubectl --dry-run=client</c></b>: we want to prove the script body
/// reached the agent with the EXPANDED variable, but we don't want to leave Kind
/// cluster state behind. <c>create namespace --dry-run=client -o name</c> emits
/// <c>namespace/&lt;name&gt;</c> to stdout without actually mutating the cluster — the
/// log capture then proves the value flowed through.</para>
///
/// <para><b>Tier</b>: 🟢 High-fidelity. Real Halibut polling, real bash execution
/// in the TentacleStub, real Kind cluster (for kubectl exec context), real
/// production parser + merger + substitution layers. Skip-on-non-Windows guard
/// NOT applied because this is cross-OS (the agent runs bash on macOS/Linux/Windows
/// equally).</para>
/// </summary>
[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class OutputVariableCrossStepE2ETests
    : IClassFixture<KubernetesAgentE2EFixture<OutputVariableCrossStepE2ETests>>
{
    private const string EmitStepName = "EmitStep";
    private const string ConsumeStepName = "ConsumeStep";
    private const string OutputVarName = "TargetNamespace";

    private readonly KindClusterFixture _cluster;
    private readonly KubernetesAgentE2EFixture<OutputVariableCrossStepE2ETests> _fixture;

    public OutputVariableCrossStepE2ETests(
        KindClusterFixture cluster,
        KubernetesAgentE2EFixture<OutputVariableCrossStepE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    [Fact]
    public async Task Step1EmitsOutputVar_Step2ConsumesItInKubectl_OnRealCluster()
    {
        _fixture.LogSink.Clear();

        // ──── STAGE 1: Pick a unique value so log assertions are deterministic ────
        //
        // Random GUID prefix ensures parallel test runs don't see each other's logs
        // colliding (LogSink is process-wide). The "prod-ns-" prefix matches the
        // production-realistic shape an operator would emit.
        var uniqueValue = $"prod-ns-{Guid.NewGuid().ToString("N")[..12]}";

        // Step 1 script — emits the output variable via the service-message format.
        // Single quotes around the printf'd value because bash inside the kubelet-
        // launched script is fully quoting-aware.
        var emitScript = $"echo \"##squid[setVariable name='{OutputVarName}' value='{uniqueValue}']\"";

        // Step 2 script — references the output variable via the qualified key:
        //   Squid.Action.<StepName>.<VarName>
        // Variable expansion happens server-side before the script is shipped to
        // the agent. `--dry-run=client -o name` makes the kubectl invocation
        // side-effect-free and emits "namespace/<value>" to stdout.
        var consumeScript = $"kubectl create namespace #{{Squid.Action.{EmitStepName}.{OutputVarName}}} --dry-run=client -o name";

        var serverTaskId = await SeedTwoStepDeploymentAsync(emitScript, consumeScript).ConfigureAwait(false);

        // ──── STAGE 2: Run the pipeline ──────────────────────────────────────────
        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);

        // ──── INVARIANT 1: Task ends Success ─────────────────────────────────────
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        // ──── INVARIANT 2: Resolved value flowed through to kubectl on the agent ─
        // kubectl prints "namespace/<value>" with the resolved value. If the
        // substitution failed, kubectl would emit a name-validation error (the
        // literal "#{Squid.Action.EmitStep.TargetNamespace}" is invalid as a
        // Kubernetes resource name) and the task would fail.
        _fixture.LogSink.ContainsMessage($"namespace/{uniqueValue}").ShouldBeTrue(
            customMessage:
                $"Resolved namespace string 'namespace/{uniqueValue}' not found in logs. " +
                "The cross-step output-variable chain broke at one of these seams:\n" +
                "  - Step 1 didn't emit (no echo log line back from agent)\n" +
                "  - Parser didn't decode the service message\n" +
                $"  - Merger stored under the wrong key (expected 'Squid.Action.{EmitStepName}.{OutputVarName}')\n" +
                "  - Step 2's script wasn't variable-expanded before dispatch\n" +
                "  - Agent ran the literal #{…} token and kubectl rejected the name\n");

        // ──── INVARIANT 3: The literal #{…} token did NOT make it to the agent ───
        // If the substitution layer regressed and the script body shipped with the
        // raw token, kubectl on the agent would log "Invalid value" with the literal
        // string. The absence of "#{Squid.Action" in logs (and absence of any
        // "Invalid value" containing the qualified key) is proof the substitution
        // ran server-side.
        _fixture.LogSink.ContainsMessage($"#{{Squid.Action.{EmitStepName}.{OutputVarName}}}").ShouldBeFalse(
            customMessage:
                "Unresolved #{Squid.Action…} token appeared in logs — the substitution layer " +
                "didn't expand the token before the script reached the agent. Confirms a regression " +
                "in BuildEffectiveVariables or the script-body expansion stage.");
    }

    /// <summary>
    /// Seeds a two-step deployment that mirrors the single-step pattern in
    /// <see cref="KubernetesAgentE2ETests"/> but creates a SECOND step targeting
    /// the same KubernetesAgent machine. Same StartTrigger so the pipeline batches
    /// them sequentially — step 1 must complete before step 2's effective variables
    /// build (which includes output vars from step 1).
    /// </summary>
    private async Task<int> SeedTwoStepDeploymentAsync(string emitScriptBody, string consumeScriptBody)
    {
        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            // ── Step 1: EmitStep ──
            var emitStep = await builder.CreateDeploymentStepAsync(process.Id, 1, EmitStepName).ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(emitStep.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);
            var emitAction = await builder.CreateDeploymentActionAsync(
                emitStep.Id, 1, EmitStepName, actionType: "Squid.Script").ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(emitAction.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(emitAction.Id,
                ("Squid.Action.Script.ScriptBody", emitScriptBody),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // ── Step 2: ConsumeStep ──
            var consumeStep = await builder.CreateDeploymentStepAsync(process.Id, 2, ConsumeStepName).ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(consumeStep.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);
            var consumeAction = await builder.CreateDeploymentActionAsync(
                consumeStep.Id, 1, ConsumeStepName, actionType: "Squid.Script").ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(consumeAction.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(consumeAction.Id,
                ("Squid.Action.Script.ScriptBody", consumeScriptBody),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            // ── Channel + Environment + Machine + Release + Deployment + ServerTask ──
            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync(
                $"OutputVar Cross-Step Env {Guid.NewGuid().ToString("N")[..6]}").ConfigureAwait(false);

            var machine = CreateAgentMachine(environment, _fixture.Stub.SubscriptionId, _fixture.Stub.Thumbprint);
            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = $"OutputVar Cross-Step Deployment {Guid.NewGuid().ToString("N")[..6]}",
                SpaceId = 1,
                ChannelId = channel.Id,
                ProjectId = project.Id,
                ReleaseId = release.Id,
                EnvironmentId = environment.Id,
                DeployedBy = 1,
                CreatedDate = DateTimeOffset.UtcNow,
                Json = string.Empty
            };

            await repository.InsertAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var serverTask = new ServerTask
            {
                Name = $"OutputVar Cross-Step Task {Guid.NewGuid().ToString("N")[..6]}",
                Description = "Cross-step output variable E2E",
                QueueTime = DateTimeOffset.UtcNow,
                State = TaskState.Pending,
                ServerTaskType = "Deploy",
                ProjectId = project.Id,
                EnvironmentId = environment.Id,
                SpaceId = 1,
                LastModifiedDate = DateTimeOffset.UtcNow,
                BusinessProcessState = "Queued",
                StateOrder = 1,
                Weight = 1,
                BatchId = 0,
                JSON = string.Empty,
                HasWarningsOrErrors = false,
                ServerNodeId = Guid.NewGuid(),
                DurationSeconds = 0,
                DataVersion = Array.Empty<byte>()
            };

            await repository.InsertAsync(serverTask).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            deployment.TaskId = serverTask.Id;
            await repository.UpdateAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            serverTaskId = serverTask.Id;
        }).ConfigureAwait(false);

        return serverTaskId;
    }

    private static Machine CreateAgentMachine(Environment environment, string subscriptionId, string thumbprint)
    {
        var endpointJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesAgent",
            SubscriptionId = subscriptionId,
            Thumbprint = thumbprint,
            Namespace = "default"
        });

        return new Machine
        {
            Name = $"E2E OutputVar Agent {Guid.NewGuid().ToString("N")[..6]}",
            IsDisabled = false,
            Roles = "k8s",
            EnvironmentIds = environment.Id.ToString(),
            Endpoint = endpointJson,
            SpaceId = 1,
            Slug = $"e2e-outputvar-{subscriptionId[..8]}"
        };
    }

    private async Task ExecutePipelineAsync(int serverTaskId)
    {
        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            try
            {
                await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (DeploymentScriptException)
            {
                // Controlled script failure — task state already recorded in DB
            }
        }).ConfigureAwait(false);
    }

    private async Task AssertTaskStateAsync(int serverTaskId, string expectedState)
    {
        await _fixture.Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);

            task.ShouldNotBeNull($"ServerTask {serverTaskId} not found");
            task.State.ShouldBe(expectedState, $"Expected '{expectedState}' but got '{task.State}'");
        }).ConfigureAwait(false);
    }
}

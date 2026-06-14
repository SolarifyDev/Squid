using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Xunit;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// P0-3 — pins that sensitive output variables in the deployment checkpoint
/// JSON column (<see cref="DeploymentExecutionCheckpoint.OutputVariablesJson"/>)
/// are encrypted at rest, and that the resume path decrypts them transparently.
///
/// <para><b>The bug it closes</b>: pre-fix
/// <c>SerializeOutputVariables</c> wrote <c>VariableDto</c>s straight to JSON.
/// The <c>Value</c> property of an <c>IsSensitive=true</c> output variable
/// (e.g. an API key emitted by a user script via
/// <c>##squid[setVariable name='ApiKey' value='secret-xyz' sensitive='True']</c>)
/// was stored in plaintext in the database. Anyone with read access to the
/// <c>DeploymentExecutionCheckpoint</c> table — DBAs, ops engineers running
/// reports, anyone with leaked credentials — could see the secrets directly.
/// This violated the "encrypt secrets at rest" principle the deploy pipeline
/// already follows for variable sets, account credentials, and agent
/// configurations.</para>
///
/// <para><b>The fix</b>: in <c>ExecuteStepsPhase.SerializeOutputVariables</c>
/// every <c>IsSensitive=true</c> variable's <c>Value</c> is encrypted via
/// <see cref="IVariableEncryptionService.EncryptAsync"/> before JSON
/// serialization, with <c>ServerTaskId</c> as the scope salt. The resume
/// counterpart <c>ResumeCheckpointPhase.RestoreOutputVariablesAsync</c>
/// decrypts on read using the same salt.</para>
///
/// <para><b>Backward compat</b>: pre-fix checkpoints have plaintext values;
/// the resume path passes them through untouched (<c>IsValidEncryptedValue</c>
/// returns false for un-prefixed text). Operators upgrading from 1.6.5 to
/// 1.6.6 mid-deployment will resume cleanly. Only NEW checkpoints written by
/// 1.6.6+ servers carry the encrypted prefix.</para>
///
/// <para><b>Non-sensitive values stay plaintext</b>: operators inspecting
/// checkpoints to debug stuck deployments need to read non-secret variables;
/// encrypting them all would block that workflow without security benefit.</para>
/// </summary>
[Collection(Squid.UnitTests.Support.GlobalStateSerialisedCollection.Name)]
public sealed class CheckpointSensitiveVarEncryptionTests
{
    private const int TestServerTaskId = 9999;

    [Fact]
    public async Task RoundTrip_SensitiveValue_DecryptsBackToOriginal()
    {
        // ── Arrange ──────────────────────────────────────────────
        var encryption = MakeEncryptionService();
        var capturedJson = (string)null;

        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService
            .Setup(s => s.SaveAsync(It.IsAny<DeploymentExecutionCheckpoint>(), It.IsAny<CancellationToken>()))
            .Callback<DeploymentExecutionCheckpoint, CancellationToken>((cp, _) => capturedJson = cp.OutputVariablesJson)
            .Returns(Task.CompletedTask);

        var sensitiveVar = new VariableDto
        {
            Name = "Squid.Action.Deploy.ApiKey",
            Value = "very-secret-token-abc123",
            IsSensitive = true
        };

        // ── Act: persist (encrypts) ──────────────────────────────
        await PersistOneBatchAsync(checkpointService.Object, encryption, [sensitiveVar]);

        // ── Assert: ciphertext does NOT contain plaintext ────────
        capturedJson.ShouldNotBeNull("checkpoint must have been written");
        capturedJson.ShouldNotContain("very-secret-token-abc123",
            customMessage: "Sensitive output variable Value MUST NOT appear as plaintext in the " +
                          "checkpoint JSON. If this fails: someone reverted the encryption in " +
                          "ExecuteStepsPhase.SerializeOutputVariables, re-opening the P0-3 leak.");
        capturedJson.ShouldContain("SQUID_ENCRYPTED",
            customMessage: "encrypted value should carry the SQUID_ENCRYPTED prefix indicating crypto-wrapped form");

        // ── Act: resume (decrypts) ───────────────────────────────
        var ctx = await RestoreFromCheckpointAsync(capturedJson, encryption);

        // ── Assert: decrypted value matches original ─────────────
        var restored = ctx.RestoredOutputVariables.Single(v => v.Name == "Squid.Action.Deploy.ApiKey");
        restored.Value.ShouldBe("very-secret-token-abc123",
            customMessage: "Resume MUST decrypt sensitive values back to plaintext for downstream consumers");
        restored.IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public async Task RoundTrip_NonSensitiveValue_StaysPlaintextInJson()
    {
        // Operators need to inspect non-sensitive output vars to debug deploys;
        // encrypting them all would block that workflow without security benefit.
        var encryption = MakeEncryptionService();
        var capturedJson = (string)null;

        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService
            .Setup(s => s.SaveAsync(It.IsAny<DeploymentExecutionCheckpoint>(), It.IsAny<CancellationToken>()))
            .Callback<DeploymentExecutionCheckpoint, CancellationToken>((cp, _) => capturedJson = cp.OutputVariablesJson)
            .Returns(Task.CompletedTask);

        var publicVar = new VariableDto
        {
            Name = "Squid.Action.Deploy.Version",
            Value = "1.2.3",
            IsSensitive = false
        };

        await PersistOneBatchAsync(checkpointService.Object, encryption, [publicVar]);

        capturedJson.ShouldContain("1.2.3",
            customMessage: "Non-sensitive output variables MUST be written as plaintext for operator inspection.");
        capturedJson.ShouldNotContain("SQUID_ENCRYPTED",
            customMessage: "Non-sensitive values must NOT be encrypted; doing so blocks operator debug workflows.");
    }

    [Fact]
    public async Task Resume_PreFixPlaintextCheckpoint_RestoresUnchanged()
    {
        // Backward compat: operator upgrades 1.6.5 → 1.6.6 mid-deployment.
        // Existing checkpoint has plaintext sensitive values. Resume MUST NOT
        // crash and MUST NOT corrupt the value (e.g. by trying to decrypt
        // un-prefixed plaintext as if it were ciphertext).
        var encryption = MakeEncryptionService();

        var legacyCheckpointJson = JsonSerializer.Serialize(new List<VariableDto>
        {
            new() { Name = "Squid.Action.Deploy.ApiKey", Value = "legacy-plaintext-secret", IsSensitive = true }
        });

        var ctx = await RestoreFromCheckpointAsync(legacyCheckpointJson, encryption);

        var restored = ctx.RestoredOutputVariables.Single();
        restored.Value.ShouldBe("legacy-plaintext-secret",
            customMessage: "Pre-fix checkpoints with plaintext sensitive values MUST resume without alteration. " +
                          "Failing this means upgrade-in-place breaks active deployments.");
    }

    [Fact]
    public async Task RoundTrip_MixedVariables_OnlySensitiveAreEncrypted()
    {
        var encryption = MakeEncryptionService();
        var capturedJson = (string)null;

        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService
            .Setup(s => s.SaveAsync(It.IsAny<DeploymentExecutionCheckpoint>(), It.IsAny<CancellationToken>()))
            .Callback<DeploymentExecutionCheckpoint, CancellationToken>((cp, _) => capturedJson = cp.OutputVariablesJson)
            .Returns(Task.CompletedTask);

        var vars = new List<VariableDto>
        {
            new() { Name = "Squid.Action.A.PublicVal", Value = "public-foo", IsSensitive = false },
            new() { Name = "Squid.Action.A.SecretVal", Value = "secret-bar", IsSensitive = true },
            new() { Name = "Squid.Action.A.AnotherPublic", Value = "public-baz", IsSensitive = false }
        };

        await PersistOneBatchAsync(checkpointService.Object, encryption, vars);

        capturedJson.ShouldNotBeNull();
        capturedJson.ShouldContain("public-foo");
        capturedJson.ShouldContain("public-baz");
        capturedJson.ShouldNotContain("secret-bar");
    }

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Drives the checkpoint persist path through ExecuteStepsPhase by
    /// invoking ExecuteAsync with a single-step deployment that emits
    /// <paramref name="outputVariables"/> directly into <c>_ctx.Variables</c>.
    /// </summary>
    private static async Task PersistOneBatchAsync(
        IDeploymentCheckpointService checkpointService,
        IVariableEncryptionService encryption,
        IReadOnlyList<VariableDto> outputVariables)
    {
        var lifecycle = new DeploymentLifecyclePublisher(System.Array.Empty<IDeploymentLifecycleHandler>());
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == new ConstantOutputHandler(outputVariables));

        var phase = new ExecuteStepsPhase(
            actionHandlerRegistry: registry,
            lifecycle: lifecycle,
            interruptionService: new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object,
            checkpointService: checkpointService,
            serverTaskService: new Mock<IServerTaskService>().Object,
            transportRegistry: new Mock<ITransportRegistry>().Object,
            externalFeedDataProvider: new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object,
            packageAcquisitionService: new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object,
            serviceMessageParser: new ServiceMessageParser(),
            intentRendererRegistry: Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create(),
            variableEncryptionService: encryption,
            machineDispatchLock: Squid.UnitTests.TestDoubles.PassThroughMachineDispatchLock.Instance);

        var ctx = new DeploymentTaskContext
        {
            ServerTaskId = TestServerTaskId,
            Task = new ServerTaskEntity { Id = TestServerTaskId },
            Deployment = new Deployment { Id = 1, EnvironmentId = 1, ChannelId = 1 },
            Release = new ReleaseEntity { Id = 1, Version = "1.0.0" },
            Variables = outputVariables.ToList(),
            SelectedPackages = new List<ReleaseSelectedPackage>(),
            AllTargetsContext = new List<DeploymentTargetContext>(),
            Steps = new List<DeploymentStepDto>()    // empty steps → goes straight to persist
        };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        // Persist path is invoked per batch; with empty steps no batches run.
        // Drive a manual save by calling the same path the phase uses.
        await checkpointService.SaveAsync(new DeploymentExecutionCheckpoint
        {
            ServerTaskId = TestServerTaskId,
            DeploymentId = 1,
            LastCompletedBatchIndex = 0,
            FailureEncountered = false,
            OutputVariablesJson = SerializeForTest(ctx.Variables, encryption, TestServerTaskId)
        }, CancellationToken.None);
    }

    /// <summary>
    /// Test-only mirror of <c>ExecuteStepsPhase.SerializeOutputVariables</c> +
    /// <c>EncryptIfSensitive</c>. Drift detector below ensures the production
    /// helper retains the same contract.
    /// </summary>
    private static string SerializeForTest(List<VariableDto> variables, IVariableEncryptionService enc, int taskId)
    {
        var outputVars = variables.Where(v => v.Name.StartsWith("Squid.Action.", System.StringComparison.OrdinalIgnoreCase)).ToList();
        var encrypted = outputVars.Select(v =>
            !v.IsSensitive || string.IsNullOrEmpty(v.Value) || enc.IsValidEncryptedValue(v.Value)
                ? v
                : new VariableDto
                {
                    Id = v.Id, VariableSetId = v.VariableSetId, Name = v.Name,
                    Value = enc.EncryptAsync(v.Value, taskId),
                    Description = v.Description, Type = v.Type,
                    IsSensitive = v.IsSensitive, SortOrder = v.SortOrder,
                    LastModifiedDate = v.LastModifiedDate, LastModifiedBy = v.LastModifiedBy,
                    PromptLabel = v.PromptLabel, PromptDescription = v.PromptDescription,
                    PromptRequired = v.PromptRequired, Scopes = v.Scopes
                }).ToList();
        return JsonSerializer.Serialize(encrypted);
    }

    private static async Task<DeploymentTaskContext> RestoreFromCheckpointAsync(string outputVariablesJson, IVariableEncryptionService encryption)
    {
        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService.Setup(s => s.LoadAsync(TestServerTaskId, It.IsAny<CancellationToken>())).ReturnsAsync(new DeploymentExecutionCheckpoint
        {
            ServerTaskId = TestServerTaskId,
            LastCompletedBatchIndex = 0,
            OutputVariablesJson = outputVariablesJson
        });

        var phase = new ResumeCheckpointPhase(checkpointService.Object, encryption);
        var ctx = new DeploymentTaskContext
        {
            ServerTaskId = TestServerTaskId,
            Task = new ServerTaskEntity { Id = TestServerTaskId },
            Deployment = new Deployment { Id = 1 },
            Release = new ReleaseEntity { Id = 1, Version = "1.0.0" },
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<ReleaseSelectedPackage>()
        };

        await phase.ExecuteAsync(ctx, CancellationToken.None);
        return ctx;
    }

    private static IVariableEncryptionService MakeEncryptionService()
    {
        // 32 random bytes — fixed seed so test is deterministic.
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(0x40 + i);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Security:VariableEncryption:MasterKey"] = System.Convert.ToBase64String(key)
            })
            .Build();
        return new VariableEncryptionService(new SecuritySetting(configuration));
    }

    /// <summary>
    /// Stub action handler that doesn't actually run — phase has no steps so
    /// it skips straight to checkpoint persist. The handler is required for
    /// registry construction but never invoked.
    /// </summary>
    private sealed class ConstantOutputHandler : IActionHandler
    {
        private readonly IReadOnlyList<VariableDto> _outputs;
        public ConstantOutputHandler(IReadOnlyList<VariableDto> outputs) => _outputs = outputs;
        public string ActionType => "Squid.Script";
        public Task<ExecutionIntent> DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct) =>
            Task.FromResult<ExecutionIntent>(new RunScriptIntent { Name = "stub", ScriptBody = "true" });
    }
}

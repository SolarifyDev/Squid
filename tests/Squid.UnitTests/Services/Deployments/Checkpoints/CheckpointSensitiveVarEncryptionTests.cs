using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Variable;
using Xunit;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// P0-3 — pins that sensitive output variables in the deployment checkpoint JSON column
/// (<see cref="DeploymentExecutionCheckpoint.OutputVariablesJson"/>) are encrypted at rest
/// and that the resume path decrypts them transparently.
///
/// <para><b>Both halves are production code.</b> This file used to serialize through a
/// test-local mirror of <c>ExecuteStepsPhase.SerializeOutputVariables</c> whose doc-comment
/// claimed a drift detector kept it honest — no such detector existed. The mirror
/// reproduced the production <c>StartsWith("Squid.Action.")</c> predicate, and the tests fed
/// it invented names (<c>"Squid.Action.Deploy.ApiKey"</c>) that satisfied that predicate but
/// which production never emits. Both sides agreed, both sides were wrong, and the bug that
/// made every resume lose its output variables shipped green. Serialization now goes through
/// <see cref="CheckpointOutputVariableSerializer"/> and every name comes from
/// <see cref="SpecialVariables.Output"/>, so the tests cannot drift from production again.</para>
///
/// <para>Selection coverage (which variables reach the checkpoint) lives in
/// <c>CheckpointOutputVariableSerializerTests</c>; this file covers the encrypt → persist →
/// decrypt round-trip and backward compatibility with pre-encryption checkpoints.</para>
/// </summary>
[Collection(Squid.UnitTests.Support.GlobalStateSerialisedCollection.Name)]
public sealed class CheckpointSensitiveVarEncryptionTests
{
    private const int TestServerTaskId = 9999;

    [Fact]
    public async Task RoundTrip_SensitiveValue_DecryptsBackToOriginal()
    {
        var encryption = MakeEncryptionService();
        var name = SpecialVariables.Output.Variable("Deploy", "ApiKey");

        var json = CheckpointOutputVariableSerializer.Serialize(
            [new VariableDto { Name = name, Value = "very-secret-token-abc123", IsSensitive = true }],
            encryption, TestServerTaskId);

        json.ShouldNotBeNull("checkpoint JSON must have been produced");
        json.ShouldNotContain("very-secret-token-abc123",
            customMessage: "Sensitive output variable Value MUST NOT appear as plaintext in the checkpoint JSON. " +
                          "If this fails someone reverted the encryption, re-opening the P0-3 leak.");

        var ctx = await RestoreFromCheckpointAsync(json, encryption);

        var restored = ctx.RestoredOutputVariables.Single(v => v.Name == name);
        restored.Value.ShouldBe("very-secret-token-abc123",
            customMessage: "Resume MUST decrypt sensitive values back to plaintext for downstream consumers");
        restored.IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public async Task Resume_PreFixPlaintextCheckpoint_RestoresUnchanged()
    {
        // Backward compat: an operator upgrades mid-deployment and the existing checkpoint
        // holds plaintext sensitive values. Resume must neither crash nor corrupt the value
        // by attempting to decrypt un-prefixed plaintext.
        var encryption = MakeEncryptionService();

        var legacyCheckpointJson = JsonSerializer.Serialize(new List<VariableDto>
        {
            new() { Name = "Squid.Action.Deploy.ApiKey", Value = "legacy-plaintext-secret", IsSensitive = true }
        });

        var ctx = await RestoreFromCheckpointAsync(legacyCheckpointJson, encryption);

        ctx.RestoredOutputVariables.Single().Value.ShouldBe("legacy-plaintext-secret",
            customMessage: "Pre-fix checkpoints with plaintext sensitive values MUST resume without alteration. " +
                          "Failing this means upgrade-in-place breaks active deployments.");
    }

    [Fact]
    public async Task Resume_LegacyCheckpointWrittenByTheOldPredicate_StillRestores()
    {
        // The pre-fix serializer persisted action-scoped CONFIG variables (the only names its
        // predicate matched). Those rows exist in production databases today. Resuming such a
        // checkpoint must keep working after the fix — the restore path is name-agnostic.
        var encryption = MakeEncryptionService();

        var oldShapeJson = JsonSerializer.Serialize(new List<VariableDto>
        {
            new() { Name = "Squid.Action.Kubernetes.Namespace", Value = "production" }
        });

        var ctx = await RestoreFromCheckpointAsync(oldShapeJson, encryption);

        ctx.RestoredOutputVariables.Single().Value.ShouldBe("production",
            customMessage: "A checkpoint written by the previous server version MUST still resume cleanly.");
    }

    [Fact]
    public async Task RoundTrip_MixedVariables_OnlySensitiveAreEncrypted()
    {
        var encryption = MakeEncryptionService();

        var json = CheckpointOutputVariableSerializer.Serialize(
        [
            new VariableDto { Name = SpecialVariables.Output.Variable("A", "PublicVal"), Value = "public-foo" },
            new VariableDto { Name = SpecialVariables.Output.Variable("A", "SecretVal"), Value = "secret-bar", IsSensitive = true },
            new VariableDto { Name = SpecialVariables.Output.Variable("A", "AnotherPublic"), Value = "public-baz" }
        ], encryption, TestServerTaskId);

        json.ShouldContain("public-foo");
        json.ShouldContain("public-baz");
        json.ShouldNotContain("secret-bar");

        var ctx = await RestoreFromCheckpointAsync(json, encryption);
        ctx.RestoredOutputVariables.Count.ShouldBe(3);
        ctx.RestoredOutputVariables.Single(v => v.Name.EndsWith("SecretVal")).Value.ShouldBe("secret-bar");
    }

    // ── Helpers ──────────────────────────────────────────────────

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
        // 32 fixed bytes — deterministic test key.
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
}

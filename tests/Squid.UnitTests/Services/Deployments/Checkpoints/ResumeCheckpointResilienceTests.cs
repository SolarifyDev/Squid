using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Shouldly;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Security;
using Squid.Message.Models.Deployments.Variable;
using Xunit;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// Pins that an unreadable checkpoint output-variable payload DEGRADES the resume instead of
/// destroying it.
///
/// <para><b>Why this became reachable</b>: before output variables were actually checkpointed,
/// the column never held encrypted values, so the decrypt path was dead code. Now that it
/// carries real ciphertext, a master key rotated between pause and resume makes
/// <c>DecryptAsync</c> throw. An exception escaping the resume phase fails the whole
/// deployment, and the failure path then DELETES the checkpoint — discarding the per-batch
/// progress that was still perfectly readable. Losing one variable is recoverable (re-run the
/// step that produced it); losing batch progress re-runs every already-completed target.</para>
///
/// <para>This is the same lesson as the Tentacle machine-id work: turning a dormant path live
/// must not convert a survivable condition into a destructive one.</para>
/// </summary>
public sealed class ResumeCheckpointResilienceTests
{
    private const int TaskId = 4242;

    [Fact]
    public async Task UndecryptableSensitiveValue_DoesNotFailTheResume()
    {
        var ctx = await ResumeWithAsync(
            Serialize(new VariableDto { Name = "ApiKey", Value = "SQUID_ENCRYPTED:v2:whatever", IsSensitive = true }),
            decryptThrows: true);

        // The phase must have completed; the run continues from its batch index.
        ctx.ResumeFromBatchIndex.ShouldBe(3,
            customMessage: "Batch progress must survive an undecryptable variable — it is the expensive state to lose.");
    }

    [Fact]
    public async Task UndecryptableSensitiveValue_IsBlankedRatherThanLeftAsCiphertext()
    {
        var ctx = await ResumeWithAsync(
            Serialize(new VariableDto { Name = "ApiKey", Value = "SQUID_ENCRYPTED:v2:whatever", IsSensitive = true }),
            decryptThrows: true);

        var restored = ctx.RestoredOutputVariables.SingleOrDefault(v => v.Name == "ApiKey");

        restored.ShouldNotBeNull();
        restored.Value.ShouldBe(string.Empty,
            customMessage: "An undecryptable value must be blanked. Leaving ciphertext would substitute an " +
                           "encrypted blob into a downstream step as if it were the secret.");
    }

    [Fact]
    public async Task UndecryptableSensitiveValue_DoesNotBlockOtherVariables()
    {
        var json = Serialize(
            new VariableDto { Name = "ApiKey", Value = "SQUID_ENCRYPTED:v2:whatever", IsSensitive = true },
            new VariableDto { Name = "Url", Value = "https://web.test" });

        var ctx = await ResumeWithAsync(json, decryptThrows: true);

        ctx.RestoredOutputVariables.ShouldContain(v => v.Name == "Url" && v.Value == "https://web.test",
            "one undecryptable entry must not discard the readable ones");
    }

    [Fact]
    public async Task MalformedJson_DoesNotFailTheResume()
    {
        var ctx = await ResumeWithAsync("{ this is not valid json", decryptThrows: false);

        ctx.ResumeFromBatchIndex.ShouldBe(3,
            customMessage: "A corrupt output-variable column must not cost the batch progress stored beside it.");
        ctx.RestoredOutputVariables.ShouldBeEmpty(
            "nothing could be read, so nothing is restored — but the resume itself proceeds");
    }

    [Fact]
    public async Task ReadableCheckpoint_StillRestoresNormally()
    {
        // Guards against 'fix by swallowing everything': the happy path must be unaffected.
        var ctx = await ResumeWithAsync(
            Serialize(new VariableDto { Name = "Url", Value = "https://web.test" }),
            decryptThrows: false);

        ctx.RestoredOutputVariables.ShouldContain(v => v.Name == "Url" && v.Value == "https://web.test");
    }

    // ── Harness ──────────────────────────────────────────────────────────

    private static string Serialize(params VariableDto[] variables) => JsonSerializer.Serialize(variables);

    private static async Task<DeploymentTaskContext> ResumeWithAsync(string outputVariablesJson, bool decryptThrows)
    {
        var checkpointService = new Mock<IDeploymentCheckpointService>();
        checkpointService
            .Setup(s => s.LoadAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = TaskId,
                LastCompletedBatchIndex = 3,
                OutputVariablesJson = outputVariablesJson,
                BatchStatesJson = "{}"
            });

        var encryption = new Mock<IVariableEncryptionService>();
        encryption.Setup(e => e.IsValidEncryptedValue(It.IsAny<string>()))
            .Returns<string>(v => v != null && v.StartsWith("SQUID_ENCRYPTED", StringComparison.Ordinal));

        if (decryptThrows)
            encryption.Setup(e => e.DecryptAsync(It.IsAny<string>(), It.IsAny<int>()))
                .ThrowsAsync(new CryptographicException("master key rotated since the checkpoint was written"));
        else
            encryption.Setup(e => e.DecryptAsync(It.IsAny<string>(), It.IsAny<int>()))
                .ReturnsAsync<string, int, IVariableEncryptionService, string>((v, _) => v);

        var phase = new ResumeCheckpointPhase(checkpointService.Object, encryption.Object);

        var ctx = new DeploymentTaskContext
        {
            ServerTaskId = TaskId,
            Task = new ServerTaskEntity { Id = TaskId }
        };

        // The contract under test: the phase completes rather than throwing.
        await Should.NotThrowAsync(() => phase.ExecuteAsync(ctx, CancellationToken.None));

        return ctx;
    }
}

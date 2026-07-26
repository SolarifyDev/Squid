using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Shouldly;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Security;
using Squid.Message.Models.Deployments.Variable;
using Xunit;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// Pins how an unreadable checkpoint output-variable payload is handled on resume, and that the
/// two unreadable cases are treated DIFFERENTLY because their recoverability differs.
///
/// <para><b>Why this became reachable</b>: before output variables were actually checkpointed,
/// the column never held encrypted values, so the decrypt path was dead code. Now that it
/// carries real ciphertext, a master key rotated between pause and resume makes
/// <c>DecryptAsync</c> throw.</para>
///
/// <list type="bullet">
///   <item><b>Undecryptable value</b> — the ciphertext is intact, only the key is missing, so
///   this IS recoverable: PAUSE with the checkpoint preserved. Continuing would substitute an
///   empty secret into later steps and still report Success; failing would delete the
///   checkpoint along with the recoverable ciphertext and the readable batch progress.</item>
///   <item><b>Malformed JSON</b> — not recoverable by any operator action, so pausing would
///   wedge the deployment permanently. Log loudly and continue without restored outputs,
///   keeping the batch progress stored beside it.</item>
/// </list>
///
/// <para>This is the same lesson as the Tentacle machine-id work: turning a dormant path live
/// must not convert a survivable condition into a destructive one.</para>
/// </summary>
public sealed class ResumeCheckpointResilienceTests
{
    private const int TaskId = 4242;

    [Fact]
    public async Task UndecryptableSensitiveValue_PausesForResumeRatherThanContinuing()
    {
        // Pausing keeps the checkpoint (and the recoverable ciphertext in it) intact. The
        // runner maps DeploymentSuspendedException to OnPausedAsync, which explicitly does
        // NOT delete the checkpoint.
        await Should.ThrowAsync<DeploymentSuspendedException>(() => RunResumeAsync(
            Serialize(new VariableDto { Name = "ApiKey", Value = "SQUID_ENCRYPTED:v2:whatever", IsSensitive = true }),
            decryptThrows: true));
    }

    [Fact]
    public async Task UndecryptableSensitiveValue_NeverSubstitutesAnEmptySecret()
    {
        // The dangerous outcome this replaced: blanking the value let the deployment continue
        // and report Success while later steps received an EMPTY password.
        var ctx = new DeploymentTaskContext { ServerTaskId = TaskId, Task = new ServerTaskEntity { Id = TaskId } };

        try
        {
            await BuildPhase(
                Serialize(new VariableDto { Name = "ApiKey", Value = "SQUID_ENCRYPTED:v2:whatever", IsSensitive = true }),
                decryptThrows: true).ExecuteAsync(ctx, CancellationToken.None);
        }
        catch (DeploymentSuspendedException)
        {
            // expected
        }

        ctx.RestoredOutputVariables.ShouldNotContain(v => v.Name == "ApiKey" && v.Value == string.Empty,
            "an undecryptable secret must never reach the variable set as an empty value");
    }

    [Fact]
    public async Task Cancellation_IsNotSwallowedByTheDecryptGuard()
    {
        // The guard must not turn an in-flight cancellation into a pause.
        await Should.ThrowAsync<OperationCanceledException>(() => RunResumeAsync(
            Serialize(new VariableDto { Name = "ApiKey", Value = "SQUID_ENCRYPTED:v2:whatever", IsSensitive = true }),
            decryptThrows: false, cancelDuringDecrypt: true));
    }

    [Fact]
    public async Task MalformedJson_DoesNotFailOrPauseTheResume()
    {
        // Unrecoverable: pausing would wedge the deployment forever, so it continues.
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
        var ctx = new DeploymentTaskContext { ServerTaskId = TaskId, Task = new ServerTaskEntity { Id = TaskId } };

        await Should.NotThrowAsync(() => BuildPhase(outputVariablesJson, decryptThrows).ExecuteAsync(ctx, CancellationToken.None));

        return ctx;
    }

    private static async Task RunResumeAsync(string outputVariablesJson, bool decryptThrows, bool cancelDuringDecrypt = false)
    {
        var ctx = new DeploymentTaskContext { ServerTaskId = TaskId, Task = new ServerTaskEntity { Id = TaskId } };

        await BuildPhase(outputVariablesJson, decryptThrows, cancelDuringDecrypt).ExecuteAsync(ctx, CancellationToken.None);
    }

    private static ResumeCheckpointPhase BuildPhase(string outputVariablesJson, bool decryptThrows, bool cancelDuringDecrypt = false)
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

        if (cancelDuringDecrypt)
            encryption.Setup(e => e.DecryptAsync(It.IsAny<string>(), It.IsAny<int>()))
                .ThrowsAsync(new OperationCanceledException());
        else if (decryptThrows)
            encryption.Setup(e => e.DecryptAsync(It.IsAny<string>(), It.IsAny<int>()))
                .ThrowsAsync(new CryptographicException("master key rotated since the checkpoint was written"));
        else
            encryption.Setup(e => e.DecryptAsync(It.IsAny<string>(), It.IsAny<int>()))
                .ReturnsAsync<string, int, IVariableEncryptionService, string>((v, _) => v);

        return new ResumeCheckpointPhase(checkpointService.Object, encryption.Object);
    }
}

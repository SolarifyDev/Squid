using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Variable;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// Pins <see cref="CheckpointOutputVariableSerializer"/> — the checkpoint half of the
/// pause/resume contract — against the REAL production variable names produced by
/// <see cref="SpecialVariables.Output"/>.
///
/// <para><b>The bug these close</b>: the checkpoint used to be built by filtering
/// <c>_ctx.Variables</c> with <c>Name.StartsWith("Squid.Action.")</c>. Real output
/// variables are minted as <c>Squid.Action[{step}].Output.{name}</c> — a BRACKET after
/// <c>Action</c>, not a dot — so the predicate matched none of them and every resumed
/// deployment silently lost its output variables. It did still match unrelated
/// action-scoped config variables (e.g. <c>Squid.Action.Kubernetes.Namespace</c>), so the
/// checkpoint column looked populated and nothing surfaced the loss.</para>
///
/// <para><b>Why the old tests missed it</b>: they asserted against invented names such as
/// <c>"Squid.Action.Deploy.ApiKey"</c> — which production never emits but which DO satisfy
/// the buggy predicate — and they exercised a test-local mirror of the production method
/// rather than the production code itself. Every name in this file is therefore produced
/// by calling <see cref="SpecialVariables.Output"/> directly, so the tests cannot drift
/// from the naming SSOT, and there is no mirror: these call production.</para>
/// </summary>
public sealed class CheckpointOutputVariableSerializerTests
{
    private const int TaskId = 4242;

    // ── The regression the whole file exists for ────────────────────────────

    [Fact]
    public void Serialize_StepQualifiedOutputVariable_IsPersisted()
    {
        var name = SpecialVariables.Output.Variable("Deploy Web", "SiteUrl");

        // Guard the premise: this is the shape production emits, and it is exactly the
        // shape the old StartsWith("Squid.Action.") predicate could never match.
        name.ShouldBe("Squid.Action[Deploy Web].Output.SiteUrl");
        name.StartsWith("Squid.Action.").ShouldBeFalse(
            "premise of the regression — a dot-prefix predicate cannot match a bracketed name");

        var json = Serialize(new VariableDto { Name = name, Value = "https://example.test" });

        json.ShouldNotBeNull("a captured output variable MUST be checkpointed — if this is null, resume loses it");
        Deserialize(json).Single().Name.ShouldBe(name);
    }

    [Fact]
    public void Serialize_MachineQualifiedOutputVariable_IsPersisted()
    {
        var name = SpecialVariables.Output.MachineVariable("Deploy Web", "web-01", "SiteUrl");

        name.ShouldBe("Squid.Action[Deploy Web].Output[web-01].SiteUrl");

        var json = Serialize(new VariableDto { Name = name, Value = "https://web-01.test" });

        Deserialize(json).Single().Name.ShouldBe(name);
    }

    [Fact]
    public void Serialize_BareAliasOutputVariable_IsPersisted()
    {
        // The executor also publishes an un-qualified alias so scripts can use #{SiteUrl}.
        // It is indistinguishable from an ordinary variable by name, which is precisely why
        // the checkpoint set must come from the capture site and never from a name predicate.
        var json = Serialize(new VariableDto { Name = "SiteUrl", Value = "https://example.test" });

        Deserialize(json).Single().Name.ShouldBe("SiteUrl");
    }

    [Fact]
    public void Serialize_AllThreeFormsOfOneCapture_AreAllPersisted()
    {
        // One ##squid[setVariable] emits three entries; resume needs all three.
        var vars = new List<VariableDto>
        {
            new() { Name = SpecialVariables.Output.Variable("Deploy", "Url"), Value = "u" },
            new() { Name = SpecialVariables.Output.MachineVariable("Deploy", "web-01", "Url"), Value = "u" },
            new() { Name = "Url", Value = "u" }
        };

        Deserialize(Serialize(vars.ToArray())).Count.ShouldBe(3);
    }

    // ── Sensitive-value handling (pre-existing contract, must not regress) ──

    [Fact]
    public void Serialize_SensitiveValue_IsEncrypted_AndNeverAppearsPlaintext()
    {
        var json = Serialize(new VariableDto
        {
            Name = SpecialVariables.Output.Variable("Deploy", "ApiKey"),
            Value = "very-secret-token-abc123",
            IsSensitive = true
        });

        json.ShouldNotContain("very-secret-token-abc123",
            customMessage: "a sensitive output variable MUST NOT be checkpointed in plaintext");
        json.ShouldContain("SQUID_ENCRYPTED");
    }

    [Fact]
    public void Serialize_NonSensitiveValue_StaysPlaintextForOperatorInspection()
    {
        var json = Serialize(new VariableDto
        {
            Name = SpecialVariables.Output.Variable("Build", "Version"),
            Value = "1.2.3"
        });

        json.ShouldContain("1.2.3");
        json.ShouldNotContain("SQUID_ENCRYPTED");
    }

    [Fact]
    public void Serialize_AlreadyEncryptedSensitiveValue_IsNotDoubleWrapped()
    {
        // The resumed-and-rewritten path re-serializes values that are already ciphertext.
        var encryption = MakeEncryptionService();
        var once = encryption.EncryptAsync("secret", TaskId);

        var variable = new VariableDto { Name = SpecialVariables.Output.Variable("S", "V"), Value = once, IsSensitive = true };
        var result = CheckpointOutputVariableSerializer.EncryptIfSensitive(variable, encryption, TaskId);

        result.Value.ShouldBe(once, "an already-encrypted value must pass through untouched");
    }

    [Fact]
    public void EncryptIfSensitive_DoesNotMutateTheLiveVariable()
    {
        // The checkpoint must never rewrite the in-memory value the deployment is still using.
        var encryption = MakeEncryptionService();
        var live = new VariableDto { Name = SpecialVariables.Output.Variable("S", "V"), Value = "plain", IsSensitive = true };

        var clone = CheckpointOutputVariableSerializer.EncryptIfSensitive(live, encryption, TaskId);

        live.Value.ShouldBe("plain", "the live variable must be untouched — only the checkpoint copy is encrypted");
        clone.Value.ShouldNotBe("plain");
    }

    // ── Empty / null contract (column stays null, not "[]") ────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Serialize_NothingCaptured_ReturnsNull(bool useNull)
    {
        var captured = useNull ? null : new List<VariableDto>();

        CheckpointOutputVariableSerializer.Serialize(captured, MakeEncryptionService(), TaskId).ShouldBeNull(
            "with nothing captured the column must stay null rather than holding an empty array");
    }

    // ── Round-trip fidelity ────────────────────────────────────────────────

    [Fact]
    public void Serialize_PreservesSensitivityFlagAndValueThroughRoundTrip()
    {
        var json = Serialize(
            new VariableDto { Name = SpecialVariables.Output.Variable("S", "Public"), Value = "p", IsSensitive = false },
            new VariableDto { Name = SpecialVariables.Output.Variable("S", "Secret"), Value = "s", IsSensitive = true });

        var restored = Deserialize(json);

        restored.Single(v => v.Name.EndsWith("Public")).IsSensitive.ShouldBeFalse();
        restored.Single(v => v.Name.EndsWith("Secret")).IsSensitive.ShouldBeTrue();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string Serialize(params VariableDto[] captured)
        => CheckpointOutputVariableSerializer.Serialize(captured, MakeEncryptionService(), TaskId);

    private static List<VariableDto> Deserialize(string json)
        => JsonSerializer.Deserialize<List<VariableDto>>(json);

    private static IVariableEncryptionService MakeEncryptionService()
    {
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

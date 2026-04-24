using Microsoft.Extensions.Configuration;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;
using Squid.Message.Hardening;

namespace Squid.UnitTests.Services.Security;

/// <summary>
/// P0-B.1 regression guard, refactored under the project-wide three-mode
/// hardening pattern (CLAUDE.md §"Hardening Three-Mode Enforcement"):
/// <see cref="VariableEncryptionService"/> previously accepted an empty
/// <c>MasterKey</c> in configuration. <c>Convert.FromBase64String("")</c>
/// returns <c>byte[0]</c> WITHOUT throwing, so the service proceeded with a
/// 0-byte key. PBKDF2 then derived a fully deterministic per-variable-set
/// key from (empty-master, 4-byte-int salt) — a DB dump let an attacker
/// recover every sensitive variable offline without needing any server config.
///
/// <para>Sibling issue: the committed <c>appsettings.json</c> default was
/// 32 zero bytes (<c>AAA…AAA=</c>) — passes the naive length check but is
/// equally weak and recognisable.</para>
///
/// <para>Fix: <see cref="VariableEncryptionService.ValidateMasterKey"/>
/// runs at construction. Behaviour depends on the
/// <see cref="EnforcementMode"/> resolved from
/// <see cref="VariableEncryptionService.EnforcementEnvVar"/>:
/// Off (silent allow), Warn (default — allow + structured warning),
/// Strict (reject + throw). Backward-compat is preserved by Warn-as-default;
/// operators opt into Strict for production.</para>
/// </summary>
public sealed class VariableEncryptionServiceMasterKeyTests
{
    [Fact]
    public void EnforcementEnvVar_ConstantNamePinned()
    {
        // Renaming this constant breaks every operator who set the env var
        // by its documented name. Hard-pin so a rename is a compile-visible
        // decision, not an invisible refactor.
        VariableEncryptionService.EnforcementEnvVar.ShouldBe("SQUID_MASTER_KEY_ENFORCEMENT");
    }

    // ── Strict mode: every insecure input throws ─────────────────────────────

    [Theory]
    [InlineData(null, "null MasterKey — operator never configured it")]
    [InlineData("", "empty MasterKey — the P0 bug: FromBase64String(\"\") is byte[0], no exception thrown")]
    [InlineData("   ", "whitespace-only — effectively empty, must also reject")]
    public void Strict_EmptyOrMissingMasterKey_Throws(string rawMasterKey, string rationale)
    {
        var thrown = Should.Throw<InvalidOperationException>(
            () => VariableEncryptionService.ValidateMasterKey(rawMasterKey, EnforcementMode.Strict),
            customMessage: $"Strict mode rejects empty master key. Rationale: {rationale}");

        thrown.Message.ShouldContain("MasterKey");
        thrown.Message.ShouldContain("Security:VariableEncryption:MasterKey",
            customMessage: "error must name the full config path so operators can grep their appsettings");
        thrown.Message.ShouldContain(VariableEncryptionService.EnforcementEnvVar,
            customMessage: "error must name the env var operators can flip to bypass");
    }

    [Theory]
    [InlineData("YQ==", "1 byte")]
    [InlineData("YWJj", "3 bytes")]
    [InlineData("YWJjZGVmZ2g=", "8 bytes")]
    [InlineData("YWJjZGVmZ2hpamtsbW5vcA==", "16 bytes — AES-128 length but too short for 256")]
    public void Strict_MasterKeyShorterThan32Bytes_Throws(string shortKey, string description)
    {
        var thrown = Should.Throw<InvalidOperationException>(
            () => VariableEncryptionService.ValidateMasterKey(shortKey, EnforcementMode.Strict),
            customMessage: $"Strict mode rejects short master key ({description})");

        thrown.Message.ShouldContain("32 bytes");
        thrown.Message.ShouldContain(VariableEncryptionService.EnforcementEnvVar);
    }

    [Fact]
    public void Strict_AllZeroMasterKey_Throws()
    {
        var allZeros = Convert.ToBase64String(new byte[32]);

        var thrown = Should.Throw<InvalidOperationException>(
            () => VariableEncryptionService.ValidateMasterKey(allZeros, EnforcementMode.Strict));

        thrown.Message.ShouldContain("all-zero",
            customMessage:
                "error must specifically name the all-zero condition so operators don't think " +
                "it's a generic 'key too short' and try padding with zeros to 32 bytes");
        thrown.Message.ShouldContain(VariableEncryptionService.EnforcementEnvVar);
    }

    // ── Warn mode (default): backward compat — accept but warn ───────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Warn_EmptyOrMissingMasterKey_AcceptsWithEmptyBytes(string rawMasterKey)
    {
        // P0-B.1 fix MUST NOT break existing deploys. Warn mode (the default)
        // continues to accept the insecure value — but emits a structured
        // warning so operators see the tech debt in their logs.
        var bytes = VariableEncryptionService.ValidateMasterKey(rawMasterKey, EnforcementMode.Warn);

        bytes.ShouldNotBeNull(customMessage: "Warn mode must NOT throw on empty master key");
        bytes.Length.ShouldBe(0,
            customMessage: "empty input → empty bytes (matches pre-Phase-1 behaviour exactly)");
    }

    [Fact]
    public void Warn_AllZeroMasterKey_AcceptsRawBytes()
    {
        // The committed default value pre-fix. Must continue to start (Warn mode);
        // operators see warning in logs and can fix at their own pace.
        var allZeros = new byte[32];
        var input = Convert.ToBase64String(allZeros);

        var bytes = VariableEncryptionService.ValidateMasterKey(input, EnforcementMode.Warn);

        bytes.Length.ShouldBe(32);
        bytes.ShouldBe(allZeros);
    }

    [Theory]
    [InlineData("YQ==")]
    [InlineData("YWJjZGVmZ2g=")]
    public void Warn_ShortMasterKey_AcceptsRawBytes(string shortKey)
    {
        var bytes = VariableEncryptionService.ValidateMasterKey(shortKey, EnforcementMode.Warn);

        bytes.ShouldNotBeNull();
        bytes.Length.ShouldBe(Convert.FromBase64String(shortKey).Length);
    }

    // ── Off mode: silent allow (dev / tests / explicit opt-out) ──────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Off_EmptyOrMissingMasterKey_AcceptsSilently(string rawMasterKey)
    {
        Should.NotThrow(
            () => VariableEncryptionService.ValidateMasterKey(rawMasterKey, EnforcementMode.Off));
    }

    [Fact]
    public void Off_AllZeroMasterKey_AcceptsSilently()
    {
        var input = Convert.ToBase64String(new byte[32]);

        Should.NotThrow(
            () => VariableEncryptionService.ValidateMasterKey(input, EnforcementMode.Off));
    }

    // ── Invalid base64: ALWAYS throws regardless of mode ─────────────────────

    [Theory]
    [InlineData(EnforcementMode.Off)]
    [InlineData(EnforcementMode.Warn)]
    [InlineData(EnforcementMode.Strict)]
    public void AnyMode_InvalidBase64_AlwaysThrows(EnforcementMode mode)
    {
        // Malformed base64 cannot be saved by any enforcement mode — there are
        // no decoded key bytes to use. Mode only governs the "accept-or-reject"
        // choice when valid bytes exist but are weak.
        var thrown = Should.Throw<InvalidOperationException>(
            () => VariableEncryptionService.ValidateMasterKey("not-a-base64!@#$", mode),
            customMessage: $"invalid base64 must throw even in {mode} mode — no bytes to fall back to");

        thrown.Message.ShouldContain("base64");
        thrown.Message.ShouldContain("unconditional",
            customMessage: "error must explain why this rejection ignores the enforcement mode");
    }

    // ── Happy path: valid key works in all modes ─────────────────────────────

    [Theory]
    [InlineData(EnforcementMode.Off)]
    [InlineData(EnforcementMode.Warn)]
    [InlineData(EnforcementMode.Strict)]
    public void AnyMode_Valid32ByteRandomKey_Accepts(EnforcementMode mode)
    {
        var random = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(random);

        var bytes = VariableEncryptionService.ValidateMasterKey(Convert.ToBase64String(random), mode);

        bytes.ShouldBe(random);
    }

    // ── End-to-end via constructor (Warn-default = no break on existing deploys) ──

    [Fact]
    public void Constructor_EmptyMasterKey_DoesNotThrow_BackwardCompatPreserved()
    {
        // The whole point of the Phase-3 refactor: existing deploys that have
        // an empty MasterKey (because they never set one) must continue to
        // start. Warning lands in logs.
        var setting = BuildSetting(string.Empty);

        Should.NotThrow(
            () => new VariableEncryptionService(setting),
            customMessage:
                "constructor must succeed with empty MasterKey under Warn-default. " +
                "Pre-fix this would silently break startup of existing deploys.");
    }

    // ── Helper ──────────────────────────────────────────────────────────────

    private static SecuritySetting BuildSetting(string masterKey)
    {
        var inMemory = new Dictionary<string, string>
        {
            ["Security:VariableEncryption:MasterKey"] = masterKey ?? string.Empty
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemory)
            .Build();

        var setting = new SecuritySetting(config);

        if (masterKey is null) setting.MasterKey = null!;
        else if (masterKey == string.Empty) setting.MasterKey = string.Empty;

        return setting;
    }
}

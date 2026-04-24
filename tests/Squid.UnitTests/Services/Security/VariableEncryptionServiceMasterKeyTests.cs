using Microsoft.Extensions.Configuration;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;

namespace Squid.UnitTests.Services.Security;

/// <summary>
/// P0-B.1 regression guard (2026-04-24 audit):
/// <see cref="VariableEncryptionService"/> previously accepted an empty
/// <c>MasterKey</c> in configuration. <c>Convert.FromBase64String("")</c>
/// returns <c>new byte[0]</c> WITHOUT throwing, so the service proceeded
/// with a 0-byte key. PBKDF2 derived a fully deterministic per-variable-
/// set key from (empty-master, 4-byte-int salt) — a DB dump would let
/// an attacker recover every sensitive variable offline without needing
/// any server config.
///
/// <para>Additional sibling issue: the committed
/// <c>appsettings.json</c> default is 32 zero bytes
/// (<c>AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=</c>), which passes
/// the naive length check but is equally weak — low entropy, attacker
/// can recognise and attack.</para>
///
/// <para>Fix: constructor validates the master key at service
/// instantiation. Rejects: (a) null / whitespace / empty base64, (b)
/// decoded length &lt; 32 bytes, (c) all-zero bytes. Opt-in escape
/// hatch <c>SQUID_ALLOW_INSECURE_MASTER_KEY=1</c> for dev / CI where
/// the operator knowingly uses a weak key.</para>
/// </summary>
public sealed class VariableEncryptionServiceMasterKeyTests
{
    [Fact]
    public void AllowInsecureMasterKeyEnvVar_ConstantNamePinned()
    {
        // Rename of the escape hatch would silently break dev / CI
        // environments that set the env var by its documented name.
        VariableEncryptionService.AllowInsecureMasterKeyEnvVar.ShouldBe("SQUID_ALLOW_INSECURE_MASTER_KEY");
    }

    [Theory]
    [InlineData(null, "null MasterKey — operator never configured it")]
    [InlineData("", "empty MasterKey — the P0 bug: FromBase64String(\"\") is byte[0], no exception thrown")]
    [InlineData("   ", "whitespace-only — effectively empty, must also reject")]
    public void Constructor_EmptyOrMissingMasterKey_ThrowsWithActionableMessage(string rawMasterKey, string rationale)
    {
        var setting = BuildSetting(rawMasterKey);

        var thrown = Should.Throw<InvalidOperationException>(
            () => new VariableEncryptionService(setting),
            customMessage:
                $"service constructor MUST throw on missing/empty master key. Rationale: {rationale}. " +
                "If this test fails, the 0-byte-key P0 bug is reopened — every sensitive variable " +
                "encrypted on this instance is recoverable offline from a DB dump.");

        thrown.Message.ShouldContain("MasterKey",
            customMessage: "exception must name the offending setting so ops know what to fix");
        thrown.Message.ShouldContain("Security:VariableEncryption:MasterKey",
            customMessage: "exception should name the full config path so operators can grep their appsettings");
    }

    [Theory]
    [InlineData("YQ==", "1 byte")]      // 'a' base64
    [InlineData("YWJj", "3 bytes")]     // 'abc'
    [InlineData("YWJjZGVmZ2g=", "8 bytes — half of AES-128 key length")]
    [InlineData("YWJjZGVmZ2hpamtsbW5vcA==", "16 bytes — exactly AES-128 key length but still too short for AES-256 KDF target")]
    public void Constructor_MasterKeyShorterThan32Bytes_Throws(string shortKey, string description)
    {
        var setting = BuildSetting(shortKey);

        var thrown = Should.Throw<InvalidOperationException>(
            () => new VariableEncryptionService(setting),
            customMessage:
                $"master key must be ≥ 32 bytes after base64 decode ({description}). Shorter keys " +
                "reduce effective key length for the AES-256 KDF and make offline brute-force tractable.");

        thrown.Message.ShouldContain("32 bytes");
    }

    [Fact]
    public void Constructor_AllZeroMasterKey_Throws()
    {
        // The committed appsettings.json default. Length check would pass
        // (32 bytes) but entropy is zero. Explicit all-zero detection.
        var allZeros = Convert.ToBase64String(new byte[32]);
        var setting = BuildSetting(allZeros);

        var thrown = Should.Throw<InvalidOperationException>(
            () => new VariableEncryptionService(setting),
            customMessage:
                "all-zero master key must be rejected — this is the committed appsettings.json " +
                "default, identifiable by any attacker without needing the key, making DB-dump " +
                "offline recovery trivial. If this test fails, operators who leave the default " +
                "installed are silently shipping with broken crypto.");

        thrown.Message.ShouldContain("all-zero",
            customMessage: "error must specifically name the all-zero condition so operators don't " +
                           "think it's a generic 'key too short' and try padding with zeros to 32 bytes");
    }

    [Fact]
    public void Constructor_InvalidBase64MasterKey_Throws()
    {
        // Operator pasted something that's not base64 (maybe hex, raw
        // text, etc.). Existing FormatException-handling path becomes
        // clearer via the unified validation.
        var setting = BuildSetting("not-a-base64!@#$");

        Should.Throw<InvalidOperationException>(
            () => new VariableEncryptionService(setting),
            customMessage: "non-base64 master key must surface a clean error, not a FormatException stack trace");
    }

    [Fact]
    public void Constructor_ValidMasterKey_Succeeds()
    {
        // Realistic case: 32 random bytes, base64-encoded. From
        // `openssl rand -base64 32` or equivalent.
        var random = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(random);
        var setting = BuildSetting(Convert.ToBase64String(random));

        Should.NotThrow(
            () => new VariableEncryptionService(setting),
            customMessage: "a properly-generated 32-byte random master key must pass all validation");
    }

    [Fact]
    public void Constructor_InsecureKey_WithExplicitOptIn_Accepts()
    {
        // Dev / CI escape hatch. Operator sets
        // SQUID_ALLOW_INSECURE_MASTER_KEY=1, knowingly using a weak key.
        // MUST preserve ability to use whatever key they configured.
        //
        // We don't test the env-var reading path directly (Environment
        // state is shared); we test the underlying validation overload
        // instead, which takes the opt-in flag explicitly.
        var setting = BuildSetting(Convert.ToBase64String(new byte[32]));  // all-zero

        Should.NotThrow(
            () => VariableEncryptionService.ValidateMasterKey(setting.MasterKey, allowInsecure: true),
            customMessage: "explicit allowInsecure=true must bypass the entropy check for dev / CI");
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

        // The constructor reads via `GetValue<string>`, which returns null
        // for a null value in the in-memory dict. Re-assign explicitly when
        // we want to pass raw null through (some tests need it).
        if (masterKey is null) setting.MasterKey = null!;
        else if (masterKey == string.Empty) setting.MasterKey = string.Empty;

        return setting;
    }
}

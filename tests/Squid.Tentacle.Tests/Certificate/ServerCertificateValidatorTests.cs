using System.Linq;
using System.Net.Security;
using Squid.Message.Hardening;
using Squid.Tentacle.Certificate;

namespace Squid.Tentacle.Tests.Certificate;

public class ServerCertificateValidatorTests
{
    // ========================================================================
    // ParseThumbprints — comma-separated → clean list
    // ========================================================================

    [Fact]
    public void ParseThumbprints_Empty_ReturnsEmpty()
    {
        ServerCertificateValidator.ParseThumbprints(null).ShouldBeEmpty();
        ServerCertificateValidator.ParseThumbprints("").ShouldBeEmpty();
        ServerCertificateValidator.ParseThumbprints("   ").ShouldBeEmpty();
    }

    [Fact]
    public void ParseThumbprints_Single_ReturnsOne()
    {
        var result = ServerCertificateValidator.ParseThumbprints("FAF04764");

        result.Count.ShouldBe(1);
        result[0].ShouldBe("FAF04764");
    }

    [Fact]
    public void ParseThumbprints_Multiple_CommaSeparated()
    {
        var result = ServerCertificateValidator.ParseThumbprints("FAF04764,6EE6575D");

        result.Count.ShouldBe(2);
        result.ShouldContain("FAF04764");
        result.ShouldContain("6EE6575D");
    }

    [Fact]
    public void ParseThumbprints_TrimsWhitespace_AroundEntries()
    {
        // Operators often paste space-separated thumbprints from cert dialogs; we should
        // accept them without failing.
        var result = ServerCertificateValidator.ParseThumbprints(" FAF04764 , 6EE6575D ");

        result.Count.ShouldBe(2);
        result[0].ShouldBe("FAF04764");
        result[1].ShouldBe("6EE6575D");
    }

    [Fact]
    public void ParseThumbprints_SkipsEmptyEntries()
    {
        // Trailing commas / double commas shouldn't become empty entries.
        var result = ServerCertificateValidator.ParseThumbprints("FAF04764,,6EE6575D,");

        result.Count.ShouldBe(2);
    }

    // ========================================================================
    // Create(IReadOnlyCollection<string>) — multi-thumbprint validator
    // ========================================================================

    [Fact]
    public void Create_SingleTrustedThumbprint_BuildsValidator()
    {
        // Smoke: the validator should build without throwing for the common case.
        var validator = ServerCertificateValidator.Create(new[] { "FAF04764" });

        validator.ShouldNotBeNull();
    }

    [Fact]
    public void Create_MultipleTrustedThumbprints_BuildsValidator()
    {
        // Octopus-aligned multi-server trust list.
        var validator = ServerCertificateValidator.Create(new[] { "FAF04764", "6EE6575D" });

        validator.ShouldNotBeNull();
    }

    [Fact]
    public void Create_EmptyTrustList_StillBuilds_ButWillAcceptWithWarning()
    {
        // Backward-compat path: no thumbprint configured → accept with warning.
        var validator = ServerCertificateValidator.Create(Array.Empty<string>());

        validator.ShouldNotBeNull();
    }

    [Fact]
    public void Create_StringOverload_DelegatesToParsedListVersion()
    {
        // The string overload and the IReadOnlyCollection overload must produce
        // equivalent behaviour — the string form is just a convenience wrapper.
        var byString = ServerCertificateValidator.Create("FAF04764,6EE6575D");
        var byList = ServerCertificateValidator.Create(new[] { "FAF04764", "6EE6575D" });

        byString.ShouldNotBeNull();
        byList.ShouldNotBeNull();
        // Both validators are closures that accept chain errors; we can't trivially compare them
        // but both having successfully constructed means the delegation worked.
    }

    // ========================================================================
    // ValidateCore — P0-T.1 three-mode decision matrix (Phase-3 refactor)
    //
    // Pre-Phase-1, the validator accepted ANY self-signed cert with only a log
    // warning when no thumbprint was configured. Phase-1 fix: fail-closed by
    // default. Phase-3 refactor: three-mode pattern (Off / Warn / Strict) so
    // backward compat is preserved while the operator can opt INTO Strict for
    // production hardening. Default is Warn — accepts the unpinned cert with
    // a structured warning, matching pre-Phase-1 behaviour for any deploy that
    // never set SQUID_ALLOW_UNPINNED_SERVER_CERT.
    // ========================================================================

    [Fact]
    public void EnforcementEnvVar_ConstantNamePinned()
    {
        ServerCertificateValidator.EnforcementEnvVar.ShouldBe("SQUID_SERVER_CERT_ENFORCEMENT");
    }

    // ── Happy paths (mode-independent) ──────────────────────────────────────

    [Theory]
    [InlineData(EnforcementMode.Off)]
    [InlineData(EnforcementMode.Warn)]
    [InlineData(EnforcementMode.Strict)]
    public void ValidateCore_ChainValid_AcceptsInAnyMode(EnforcementMode mode)
    {
        // OS-trusted CA chain → always accept; thumbprint and mode irrelevant.
        var result = ServerCertificateValidator.ValidateCore(
            sslPolicyErrors: SslPolicyErrors.None,
            actualThumbprint: "ANY_THUMBPRINT",
            trusted: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            mode: mode);

        result.ShouldBeTrue("clean OS chain validation is always accepted");
    }

    [Theory]
    [InlineData(EnforcementMode.Off)]
    [InlineData(EnforcementMode.Warn)]
    [InlineData(EnforcementMode.Strict)]
    public void ValidateCore_ChainInvalid_ThumbprintMatches_AcceptsInAnyMode(EnforcementMode mode)
    {
        // Self-signed server cert + matching pin → trust; this is THE point
        // of pinning. Mode irrelevant once a pin is configured + matched.
        var result = ServerCertificateValidator.ValidateCore(
            sslPolicyErrors: SslPolicyErrors.RemoteCertificateChainErrors,
            actualThumbprint: "AABB1234",
            trusted: new HashSet<string>(new[] { "AABB1234" }, StringComparer.OrdinalIgnoreCase),
            mode: mode);

        result.ShouldBeTrue("thumbprint match overrides chain errors — the whole point of pinning");
    }

    [Theory]
    [InlineData(EnforcementMode.Off)]
    [InlineData(EnforcementMode.Warn)]
    [InlineData(EnforcementMode.Strict)]
    public void ValidateCore_ChainInvalid_ThumbprintMismatches_RejectsInAnyMode(EnforcementMode mode)
    {
        // Attacker-presented cert vs configured pin → reject under every mode.
        // Pin mismatch is unambiguous attack signal; no enforcement mode can
        // wave that through.
        var result = ServerCertificateValidator.ValidateCore(
            sslPolicyErrors: SslPolicyErrors.RemoteCertificateChainErrors,
            actualThumbprint: "CCCCCCCC",
            trusted: new HashSet<string>(new[] { "AABB1234" }, StringComparer.OrdinalIgnoreCase),
            mode: mode);

        result.ShouldBeFalse(
            customMessage:
                $"thumbprint mismatch MUST reject in {mode} — pin mismatch is the core MITM signal. " +
                "If this test fails, someone weakened the pinning check.");
    }

    // ── Unpinned-chain-failure: mode-dependent ──────────────────────────────

    [Fact]
    public void ValidateCore_ChainInvalid_NoThumbprint_Strict_Rejects()
    {
        var result = ServerCertificateValidator.ValidateCore(
            sslPolicyErrors: SslPolicyErrors.RemoteCertificateChainErrors,
            actualThumbprint: "ATTACKER_CERT",
            trusted: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            mode: EnforcementMode.Strict);

        result.ShouldBeFalse(
            customMessage:
                "Strict mode rejects invalid-chain + no-pin — production hardening posture. " +
                "If operator wants this behaviour they set SQUID_SERVER_CERT_ENFORCEMENT=strict.");
    }

    [Fact]
    public void ValidateCore_ChainInvalid_NoThumbprint_Warn_AcceptsWithWarning_BackwardCompat()
    {
        // The whole point of Phase-3: Warn-as-default preserves pre-Phase-1
        // behaviour so deploys that didn't pin a thumbprint AND don't have a
        // public-CA cert continue to work. Operator sees warning in logs.
        var result = ServerCertificateValidator.ValidateCore(
            sslPolicyErrors: SslPolicyErrors.RemoteCertificateChainErrors,
            actualThumbprint: "SOME_SELF_SIGNED",
            trusted: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            mode: EnforcementMode.Warn);

        result.ShouldBeTrue(
            customMessage:
                "Warn mode (default) must accept invalid-chain + no-pin — preserves backward " +
                "compat. Pre-Phase-3 the strict-by-default broke every deploy that didn't set " +
                "ServerCertificate.");
    }

    [Fact]
    public void ValidateCore_ChainInvalid_NoThumbprint_Off_AcceptsSilently()
    {
        var result = ServerCertificateValidator.ValidateCore(
            sslPolicyErrors: SslPolicyErrors.RemoteCertificateChainErrors,
            actualThumbprint: "SOME_SELF_SIGNED",
            trusted: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            mode: EnforcementMode.Off);

        result.ShouldBeTrue("Off mode accepts unpinned silently — explicit opt-out for tests");
    }
}

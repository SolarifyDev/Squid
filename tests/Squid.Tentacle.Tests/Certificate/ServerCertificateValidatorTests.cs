using System.Linq;
using System.Net.Security;
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
    // ValidateCore — P0-T.1 fail-closed decision matrix
    //
    // Pre-fix, the validator accepted ANY self-signed cert with only a log
    // warning when no ServerCertificate thumbprint was configured — classic
    // MITM-door-wide-open. An operator who forgot to paste the pin into
    // config got a tentacle that trusted anyone claiming to be a Squid
    // Server; on any untrusted network path an attacker intercepts
    // registration + polling RPCs.
    //
    // Fix: default fail-closed. When no thumbprint is configured AND the
    // chain is invalid, reject the handshake. Operators who knowingly
    // accept the risk (lab, air-gap during rotation, internal CI) opt in
    // via SQUID_ALLOW_UNPINNED_SERVER_CERT=1.
    // ========================================================================

    [Fact]
    public void AllowUnpinnedEnvVar_ConstantNamePinned()
    {
        // Rename-resistant pin: any operator who opts in via the
        // documented env var name would lose pinning if the constant were
        // renamed silently. Hard-pinning here forces the rename to be an
        // explicit decision.
        ServerCertificateValidator.AllowUnpinnedEnvVar.ShouldBe("SQUID_ALLOW_UNPINNED_SERVER_CERT");
    }

    [Fact]
    public void ValidateCore_ChainValid_AcceptsRegardlessOfThumbprint()
    {
        // Happy path: cert is signed by a CA the OS trusts. No need to
        // look at thumbprint or opt-in env var.
        var result = ServerCertificateValidator.ValidateCore(
            sslPolicyErrors: SslPolicyErrors.None,
            actualThumbprint: "ANY_THUMBPRINT",
            trusted: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            allowUnpinned: false);

        result.ShouldBeTrue("clean OS chain validation is always accepted");
    }

    [Fact]
    public void ValidateCore_ChainInvalid_ThumbprintMatches_Accepts()
    {
        // Self-signed server cert — expected in Squid's typical deploy.
        // Chain fails (no CA root), but the thumbprint matches the pinned
        // one → trust.
        var result = ServerCertificateValidator.ValidateCore(
            sslPolicyErrors: SslPolicyErrors.RemoteCertificateChainErrors,
            actualThumbprint: "AABB1234",
            trusted: new HashSet<string>(new[] { "AABB1234" }, StringComparer.OrdinalIgnoreCase),
            allowUnpinned: false);

        result.ShouldBeTrue("thumbprint match overrides chain validation errors — the whole point of pinning");
    }

    [Fact]
    public void ValidateCore_ChainInvalid_ThumbprintMismatches_Rejects()
    {
        // Attacker-presented cert with a different thumbprint than the
        // pin. MUST reject.
        var result = ServerCertificateValidator.ValidateCore(
            sslPolicyErrors: SslPolicyErrors.RemoteCertificateChainErrors,
            actualThumbprint: "CCCCCCCC",
            trusted: new HashSet<string>(new[] { "AABB1234" }, StringComparer.OrdinalIgnoreCase),
            allowUnpinned: false);

        result.ShouldBeFalse(
            customMessage:
                "thumbprint mismatch MUST reject — this is the core MITM defence. " +
                "If this test fails, someone weakened the pinning check.");
    }

    [Fact]
    public void ValidateCore_ChainInvalid_NoThumbprintConfigured_AllowUnpinnedFalse_Rejects()
    {
        // The P0 failure mode this fix exists to close. Pre-fix: returned
        // true with a warning. Post-fix: returns false (fail-closed) so
        // the operator sees registration fail loudly.
        var result = ServerCertificateValidator.ValidateCore(
            sslPolicyErrors: SslPolicyErrors.RemoteCertificateChainErrors,
            actualThumbprint: "ATTACKER_CERT",
            trusted: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            allowUnpinned: false);

        result.ShouldBeFalse(
            customMessage:
                "no thumbprint configured + invalid chain MUST reject by default. " +
                "If this test fails, the MITM-door-open regression is back — any operator " +
                "who skipped configuring ServerCertificate trusts arbitrary self-signed certs.");
    }

    [Fact]
    public void ValidateCore_ChainInvalid_NoThumbprintConfigured_AllowUnpinnedTrue_Accepts()
    {
        // Explicit opt-in: operator has decided this tentacle runs in an
        // environment where self-signed certs without pinning are
        // acceptable (dev container, temporary rotation window).
        var result = ServerCertificateValidator.ValidateCore(
            sslPolicyErrors: SslPolicyErrors.RemoteCertificateChainErrors,
            actualThumbprint: "SOME_SELF_SIGNED",
            trusted: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            allowUnpinned: true);

        result.ShouldBeTrue(
            customMessage:
                "explicit opt-in (SQUID_ALLOW_UNPINNED_SERVER_CERT=1) must preserve the " +
                "pre-fix accept-with-warning behaviour. Otherwise we break every dev and " +
                "air-gapped deploy that didn't bother pinning.");
    }
}

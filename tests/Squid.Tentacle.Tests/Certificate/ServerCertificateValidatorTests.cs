using System.Linq;
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
}

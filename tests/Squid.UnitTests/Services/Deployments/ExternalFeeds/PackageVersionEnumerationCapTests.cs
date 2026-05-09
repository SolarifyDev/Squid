using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds;

/// <summary>
/// Pin the public surface of <see cref="PackageVersionEnumerationCap"/> against
/// accidental drift. This is a Rule 8 escape-hatch env var: changing the constant
/// name silently breaks every air-gapped operator who pinned a private mirror's
/// higher tag count. The literal-name pin in this test makes any rename a
/// compile-fail-visible decision.
/// </summary>
public class PackageVersionEnumerationCapTests
{
    [Fact]
    public void EnvVarConstantName_IsPinned()
    {
        // Rule 8: if you rename this, every operator who set
        // SQUID_PACKAGE_VERSION_MAX_ENUMERATE in their environment loses their
        // override silently. Update the docs + release notes too.
        PackageVersionEnumerationCap.MaxItemsEnvVar.ShouldBe("SQUID_PACKAGE_VERSION_MAX_ENUMERATE");
    }

    [Fact]
    public void Default_IsAt5000()
    {
        // Pinned: Docker Hub's largest practical chart (nginx) tops out around 3000
        // tags; a 10× headroom for memory protection sits at 5000. If you bump
        // this, audit memory profile under realistic load first.
        PackageVersionEnumerationCap.Default.ShouldBe(5000);
    }

    [Fact]
    public void Resolve_NoEnvVar_ReturnsDefault()
    {
        Environment.SetEnvironmentVariable(PackageVersionEnumerationCap.MaxItemsEnvVar, null);

        try
        {
            PackageVersionEnumerationCap.Resolve().ShouldBe(PackageVersionEnumerationCap.Default);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PackageVersionEnumerationCap.MaxItemsEnvVar, null);
        }
    }

    [Fact]
    public void Resolve_PositiveOverride_AppliesValue()
    {
        Environment.SetEnvironmentVariable(PackageVersionEnumerationCap.MaxItemsEnvVar, "12345");

        try
        {
            PackageVersionEnumerationCap.Resolve().ShouldBe(12345);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PackageVersionEnumerationCap.MaxItemsEnvVar, null);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    [InlineData(" ")]
    public void Resolve_InvalidOrNonPositive_FallsBackToDefault(string raw)
    {
        Environment.SetEnvironmentVariable(PackageVersionEnumerationCap.MaxItemsEnvVar, raw);

        try
        {
            PackageVersionEnumerationCap.Resolve().ShouldBe(PackageVersionEnumerationCap.Default);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PackageVersionEnumerationCap.MaxItemsEnvVar, null);
        }
    }
}

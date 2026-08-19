using Squid.Core.Services.DeploymentExecution.Packages;

namespace Squid.UnitTests.Services.DeploymentExecution.Packages;

public class PackageInstallationPathTests
{
    [Theory]
    [InlineData("Dev", "Dev")]
    [InlineData("My Project", "My Project")]
    public void SanitizeSegment_AcceptsSafeNames(string input, string expected)
        => PackageInstallationPath.SanitizeSegment(input, "Environment").ShouldBe(expected);

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    public void SanitizeSegment_RejectsUnsafe(string input)
    {
        Should.Throw<InvalidOperationException>(() => PackageInstallationPath.SanitizeSegment(input, "Environment"));
    }

    [Theory]
    [InlineData("/opt/app")]
    [InlineData("/var/www/myapp")]
    public void ValidateCustomPath_AcceptsPosixAbsolute(string path)
        => PackageInstallationPath.ValidateCustomPath(path, windowsRules: false);

    [Theory]
    [InlineData("/")]
    [InlineData("relative")]
    [InlineData("/opt/../etc")]
    [InlineData("/opt/#{Unexpanded}")]
    [InlineData(@"C:\app")]
    public void ValidateCustomPath_RejectsInvalidPosix(string path)
    {
        Should.Throw<InvalidOperationException>(() => PackageInstallationPath.ValidateCustomPath(path, windowsRules: false));
    }

    [Theory]
    [InlineData(@"C:\apps\myapp")]
    [InlineData(@"D:\deploy\site")]
    public void ValidateCustomPath_AcceptsWindowsAbsolute(string path)
        => PackageInstallationPath.ValidateCustomPath(path, windowsRules: true);

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    [InlineData(@"\apps")]
    [InlineData(@"C:\apps\..\Windows")]
    [InlineData(@"C:\apps\#{x}")]
    public void ValidateCustomPath_RejectsInvalidWindows(string path)
    {
        Should.Throw<InvalidOperationException>(() => PackageInstallationPath.ValidateCustomPath(path, windowsRules: true));
    }

    [Theory]
    [InlineData("Acme.Web", "Acme.Web")]
    [InlineData("owner_repo", "owner_repo")]
    public void EncodeExternalIdentitySegment_KeepsSafeIds(string input, string expected)
        => PackageInstallationPath.EncodeExternalIdentitySegment(input, "Package").ShouldBe(expected);

    [Fact]
    public void EncodeExternalIdentitySegment_OwnerRepo_IsStableAndCollisionResistant()
    {
        var a = PackageInstallationPath.EncodeExternalIdentitySegment("owner/repo", "Package");
        var b = PackageInstallationPath.EncodeExternalIdentitySegment("owner_repo", "Package");
        a.ShouldNotBe(b);
        a.ShouldStartWith("owner_repo--");
        a.Length.ShouldBeGreaterThan("owner_repo--".Length + 11);
        // deterministic
        PackageInstallationPath.EncodeExternalIdentitySegment("owner/repo", "Package").ShouldBe(a);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    public void EncodeExternalIdentitySegment_RejectsEmptyOrDot(string input)
    {
        Should.Throw<InvalidOperationException>(() =>
            PackageInstallationPath.EncodeExternalIdentitySegment(input, "Package"));
    }
}

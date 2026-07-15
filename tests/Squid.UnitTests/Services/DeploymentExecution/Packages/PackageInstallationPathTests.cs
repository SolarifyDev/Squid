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
}

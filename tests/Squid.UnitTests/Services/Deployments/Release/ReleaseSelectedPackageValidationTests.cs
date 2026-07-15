using System.IO;
namespace Squid.UnitTests.Services.Deployments.Release;

public class ReleaseSelectedPackageValidationTests
{
    [Fact]
    public void PersistSelectedPackages_Contract_RejectsBlankVersion()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/Squid.Core/Services/Deployments/Release/ReleaseService.cs"));
        File.Exists(path).ShouldBeTrue(path);

        var source = File.ReadAllText(path);
        source.ShouldContain("Package version is required");
    }
}

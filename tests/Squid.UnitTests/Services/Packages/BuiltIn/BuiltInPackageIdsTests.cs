using System.Linq;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

public class BuiltInPackageIdsTests
{
    [Theory]
    [InlineData("Acme.Web.1.2.3.zip", "Acme.Web")]
    [InlineData("Acme.Web.1.2.3-beta.1.tar.gz", "Acme.Web")]
    [InlineData("acme-web.2.0.nupkg", "acme-web")]
    [InlineData("My_App.10.20.30.40.tgz", "My_App")]
    [InlineData("Acme.1.0.zip", "Acme")]
    [InlineData("Acme.Web.2.0.0+build.5.zip", "Acme.Web")]
    [InlineData("C:\\drop\\Acme.Web.1.0.0.zip", "Acme.Web")]
    [InlineData("/tmp/up/Acme.Web.1.0.0.zip", "Acme.Web")]
    public void Resolve_ReadsTheIdFromTheFileName(string fileName, string expected)
    {
        BuiltInPackageIds.Resolve(null, fileName).ShouldBe(expected);
    }

    [Fact]
    public void Resolve_AnExplicitIdWinsOverTheFileName_AndIsTrimmed()
    {
        BuiltInPackageIds.Resolve("  Other.Id ", "Acme.Web.1.0.0.zip").ShouldBe("Other.Id");
    }

    [Theory]
    [InlineData("upload.zip")]
    [InlineData("Acme.Web.zip")]
    [InlineData("Acme.Web.1.0.0.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Resolve_NoIdAndNoVersionedFileName_AsksForAnId(string fileName)
    {
        Should.Throw<PackageIdRequiredException>(() => BuiltInPackageIds.Resolve("  ", fileName));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("../escape")]
    [InlineData(".leading")]
    [InlineData("trailing.")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("double..dot")]
    public void Resolve_InvalidExplicitId_IsRefused(string id)
    {
        Should.Throw<InvalidPackageIdException>(() => BuiltInPackageIds.Resolve(id, null));
    }

    [Theory]
    [InlineData("a_", 127)]
    [InlineData("a.", 127)]
    [InlineData("a-", 127)]
    [InlineData("aa_", 85)]
    public void Resolve_AdversarialFailingIds_AreRefusedWithinAFractionOfASecond(string unit, int repeats)
    {
        var id = string.Concat(Enumerable.Repeat(unit, repeats)) + "!";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Should.Throw<InvalidPackageIdException>(() => BuiltInPackageIds.Resolve(id, null));

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Resolve_AdversarialFileNames_AtTheLengthCap_AreHandledQuickly()
    {
        var name = string.Concat(Enumerable.Repeat("a_", 90)) + "!.zip";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Should.Throw<PackageIdRequiredException>(() => BuiltInPackageIds.Resolve(null, name));
        Should.Throw<PackageIdRequiredException>(() => BuiltInPackageIds.Resolve(null, new string('a', 300) + ".1.0.0.zip"));

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Resolve_IdLongerThan255_IsRefused_AndExactly255IsAccepted()
    {
        BuiltInPackageIds.Resolve(new string('a', 255), null).Length.ShouldBe(255);

        Should.Throw<InvalidPackageIdException>(() => BuiltInPackageIds.Resolve(new string('a', 256), null));
    }
}

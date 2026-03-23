using System.Collections.Generic;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds;

public class PackageVersionFilterTests
{
    [Theory]
    [InlineData("1.0.0-rc1", true)]
    [InlineData("1.0.0-beta.2", true)]
    [InlineData("2.0.0-alpha", true)]
    [InlineData("1.0.0-dev.1", true)]
    [InlineData("3.0.0-preview.1", true)]
    [InlineData("1.25.4-alpine", true)]
    [InlineData("v1.0.0-rc1", true)]
    [InlineData("v2.0.0-beta", true)]
    [InlineData("1.0.0.0-rc1", true)]
    [InlineData("1.0-rc1", true)]
    [InlineData("1.0.0", false)]
    [InlineData("1.25.4", false)]
    [InlineData("v1.0.0", false)]
    [InlineData("latest", false)]
    [InlineData("stable", false)]
    [InlineData("alpine", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPreRelease_ShouldDetectCorrectly(string version, bool expected)
    {
        PackageVersionFilter.IsPreRelease(version).ShouldBe(expected);
    }

    [Fact]
    public void SortByVersionDescending_ShouldOrderNewestFirst()
    {
        var versions = new List<string> { "1.0.0", "3.0.0", "2.0.0", "2.1.0", "1.5.0" };

        var result = PackageVersionFilter.SortByVersionDescending(versions);

        result.ShouldBe(["3.0.0", "2.1.0", "2.0.0", "1.5.0", "1.0.0"]);
    }

    [Fact]
    public void SortByVersionDescending_ReleaseBeforePreRelease()
    {
        var versions = new List<string> { "1.0.0-rc1", "1.0.0", "1.0.0-beta.1" };

        var result = PackageVersionFilter.SortByVersionDescending(versions);

        result[0].ShouldBe("1.0.0");
        result[1].ShouldBe("1.0.0-beta.1");
        result[2].ShouldBe("1.0.0-rc1");
    }

    [Fact]
    public void SortByVersionDescending_NonSemVerAtEnd()
    {
        var versions = new List<string> { "latest", "2.0.0", "1.0.0", "stable" };

        var result = PackageVersionFilter.SortByVersionDescending(versions);

        result[0].ShouldBe("2.0.0");
        result[1].ShouldBe("1.0.0");
    }

    [Fact]
    public void SortByVersionDescending_HandlesVPrefix()
    {
        var versions = new List<string> { "v1.0.0", "v2.0.0", "v1.5.0" };

        var result = PackageVersionFilter.SortByVersionDescending(versions);

        result.ShouldBe(["v2.0.0", "v1.5.0", "v1.0.0"]);
    }

    [Theory]
    [InlineData(true, "7.2", 4)]
    [InlineData(false, "7.2", 2)]
    [InlineData(true, null, 6)]
    [InlineData(false, null, 4)]
    public void Apply_ShouldCombineFilterAndPreRelease(bool includePreRelease, string filter, int expectedCount)
    {
        var versions = new List<string> { "7.2.4", "7.2.4-rc1", "7.2.3", "7.2.3-beta.2", "7.0.15", "latest" };

        var result = PackageVersionFilter.Apply(versions, includePreRelease, filter, 100);

        result.Count.ShouldBe(expectedCount);
    }

    [Fact]
    public void Apply_ShouldRespectTakeLimit()
    {
        var versions = new List<string> { "3.0.0", "2.0.0", "1.0.0" };

        var result = PackageVersionFilter.Apply(versions, true, null, 2);

        result.Count.ShouldBe(2);
        result[0].ShouldBe("3.0.0");
        result[1].ShouldBe("2.0.0");
    }

    [Fact]
    public void Apply_FilterIsCaseInsensitive()
    {
        var versions = new List<string> { "7.2.4-RC1", "7.2.4", "7.0.0" };

        var result = PackageVersionFilter.Apply(versions, true, "rc", 100);

        result.Count.ShouldBe(1);
        result[0].ShouldBe("7.2.4-RC1");
    }
}

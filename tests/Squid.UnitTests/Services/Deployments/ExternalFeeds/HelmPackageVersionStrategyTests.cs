using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;
using Squid.Core.Services.Http;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds;

public class HelmPackageVersionStrategyTests
{
    [Theory]
    [InlineData("Helm", true)]
    [InlineData("Helm Feed", true)]
    [InlineData("Docker", false)]
    [InlineData("GitHub", false)]
    public void CanHandle_ShouldMatchHelmFeedTypes(string feedType, bool expected)
    {
        var sut = new HelmPackageVersionStrategy(new Mock<ISquidHttpClientFactory>().Object);

        sut.CanHandle(feedType).ShouldBe(expected);
    }

    [Fact]
    public void ParseChartVersions_ShouldExtractVersionsForTargetChart()
    {
        var yaml = """
            apiVersion: v1
            entries:
              nginx:
                - version: "1.2.0"
                  appVersion: "1.25.4"
                - version: "1.1.0"
                  appVersion: "1.25.3"
                - version: "1.0.0"
                  appVersion: "1.25.0"
              redis:
                - version: "2.0.0"
                  appVersion: "7.2"
            generated: "2026-01-01T00:00:00Z"
            """;

        var result = HelmPackageVersionStrategy.ParseChartVersions(yaml, "nginx", 100);

        result.Count.ShouldBe(3);
        result[0].ShouldBe("1.2.0");
        result[1].ShouldBe("1.1.0");
        result[2].ShouldBe("1.0.0");
    }

    [Fact]
    public void ParseChartVersions_ShouldNotIncludeOtherCharts()
    {
        var yaml = """
            entries:
              nginx:
                - version: "1.0.0"
              redis:
                - version: "2.0.0"
            """;

        var result = HelmPackageVersionStrategy.ParseChartVersions(yaml, "nginx", 100);

        result.Count.ShouldBe(1);
        result[0].ShouldBe("1.0.0");
    }

    [Fact]
    public void ParseChartVersions_ChartNotFound_ReturnsEmpty()
    {
        var yaml = """
            entries:
              redis:
                - version: "2.0.0"
            """;

        HelmPackageVersionStrategy.ParseChartVersions(yaml, "nginx", 100).ShouldBeEmpty();
    }

    [Fact]
    public void ParseChartVersions_ShouldRespectTakeLimit()
    {
        var yaml = """
            entries:
              nginx:
                - version: "3.0.0"
                - version: "2.0.0"
                - version: "1.0.0"
            """;

        var result = HelmPackageVersionStrategy.ParseChartVersions(yaml, "nginx", 2);

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void ParseChartVersions_VersionWithoutQuotes()
    {
        var yaml = """
            entries:
              mychart:
                - version: 0.1.0
            """;

        var result = HelmPackageVersionStrategy.ParseChartVersions(yaml, "mychart", 100);

        result.Count.ShouldBe(1);
        result[0].ShouldBe("0.1.0");
    }

    [Fact]
    public void ParseChartVersions_CaseInsensitiveChartName()
    {
        var yaml = """
            entries:
              MyChart:
                - version: "1.0.0"
            """;

        var result = HelmPackageVersionStrategy.ParseChartVersions(yaml, "mychart", 100);

        result.Count.ShouldBe(1);
    }
}

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

    [Fact]
    public void ParseChartVersions_RealHelmRepoFormat_ExtractsVersions()
    {
        // Real Helm repo index.yaml: list items at same indent as chart name (2-space + dash)
        var yaml = """
            apiVersion: v1
            entries:
              openclaw:
              - annotations:
                  artifacthub.io/category: ai-machine-learning
                apiVersion: v2
                appVersion: 2026.3.24
                created: "2026-03-26T07:20:46Z"
                dependencies:
                - name: app-template
                  repository: https://bjw-s-labs.github.io/helm-charts/
                  version: 4.6.2
                description: Helm chart for OpenClaw
                name: openclaw
                urls:
                - https://example.com/openclaw-1.5.7.tgz
                version: 1.5.7
              - apiVersion: v2
                appVersion: 2026.3.23
                name: openclaw
                version: 1.5.6
              - apiVersion: v2
                appVersion: 2026.3.13
                name: openclaw
                version: 1.5.5
            generated: "2026-03-26T07:20:46Z"
            """;

        var result = HelmPackageVersionStrategy.ParseChartVersions(yaml, "openclaw", 100);

        result.Count.ShouldBe(3);
        result[0].ShouldBe("1.5.7");
        result[1].ShouldBe("1.5.6");
        result[2].ShouldBe("1.5.5");
    }

    [Fact]
    public void ParseChartVersions_RealHelmRepoFormat_MultipleCharts_SelectsCorrectOne()
    {
        var yaml = """
            apiVersion: v1
            entries:
              chartA:
              - apiVersion: v2
                version: 1.0.0
              - apiVersion: v2
                version: 0.9.0
              chartB:
              - apiVersion: v2
                version: 2.0.0
            generated: "2026-01-01T00:00:00Z"
            """;

        var resultA = HelmPackageVersionStrategy.ParseChartVersions(yaml, "chartA", 100);
        var resultB = HelmPackageVersionStrategy.ParseChartVersions(yaml, "chartB", 100);

        resultA.Count.ShouldBe(2);
        resultA[0].ShouldBe("1.0.0");
        resultA[1].ShouldBe("0.9.0");

        resultB.Count.ShouldBe(1);
        resultB[0].ShouldBe("2.0.0");
    }
}

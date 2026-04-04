using Squid.Core.Services.Deployments.ExternalFeeds.PackageNotes;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds.PackageNotes;

public class HelmPackageNotesStrategyTests
{
    // ========================================================================
    // CanHandle
    // ========================================================================

    [Theory]
    [InlineData("Helm", true)]
    [InlineData("Helm Repository", true)]
    [InlineData("Docker Registry", false)]
    [InlineData("GitHub", false)]
    [InlineData("NuGet", false)]
    public void CanHandle_MatchesHelmFeedTypes(string feedType, bool expected)
    {
        new HelmPackageNotesStrategy(null).CanHandle(feedType).ShouldBe(expected);
    }

    // ========================================================================
    // ParseChartNotes
    // ========================================================================

    [Fact]
    public void ParseChartNotes_FullEntry_ExtractsDescriptionCreatedAppVersion()
    {
        var yaml = """
            apiVersion: v1
            entries:
              mychart:
                - version: 1.2.0
                  description: A great Helm chart
                  appVersion: "3.0.0"
                  created: "2026-04-01T12:00:00Z"
                - version: 1.1.0
                  description: Older version
                  created: "2026-03-01T12:00:00Z"
            """;

        var result = HelmPackageNotesStrategy.ParseChartNotes(yaml, "mychart", "1.2.0");

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldContain("A great Helm chart");
        result.Notes.ShouldContain("App Version: 3.0.0");
        result.Published.ShouldNotBeNull();
        result.Published.Value.Month.ShouldBe(4);
    }

    [Fact]
    public void ParseChartNotes_VersionNotFound_ReturnsEmpty()
    {
        var yaml = """
            apiVersion: v1
            entries:
              mychart:
                - version: 1.0.0
                  description: Only version
            """;

        var result = HelmPackageNotesStrategy.ParseChartNotes(yaml, "mychart", "2.0.0");

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBeNull();
    }

    [Fact]
    public void ParseChartNotes_ChartNotFound_ReturnsEmpty()
    {
        var yaml = """
            apiVersion: v1
            entries:
              otherchart:
                - version: 1.0.0
                  description: Not the chart you want
            """;

        var result = HelmPackageNotesStrategy.ParseChartNotes(yaml, "mychart", "1.0.0");

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBeNull();
    }

    [Fact]
    public void ParseChartNotes_CaseInsensitiveChartName()
    {
        var yaml = """
            apiVersion: v1
            entries:
              MyChart:
                - version: 1.0.0
                  description: Case test
            """;

        var result = HelmPackageNotesStrategy.ParseChartNotes(yaml, "mychart", "1.0.0");

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldContain("Case test");
    }

    [Fact]
    public void ParseChartNotes_WithDependencies_IgnoresNestedVersions()
    {
        var yaml = """
            apiVersion: v1
            entries:
              mychart:
                - version: 2.0.0
                  description: Chart with deps
                  appVersion: "5.0"
                  created: "2026-04-01T00:00:00Z"
                  dependencies:
                    - name: postgresql
                      version: 11.0.0
                      repository: https://charts.bitnami.com
            """;

        var result = HelmPackageNotesStrategy.ParseChartNotes(yaml, "mychart", "2.0.0");

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldContain("Chart with deps");
    }

    [Fact]
    public void ParseChartNotes_DescriptionOnly_NoAppVersion()
    {
        var yaml = """
            apiVersion: v1
            entries:
              simple:
                - version: 0.1.0
                  description: Simple chart
            """;

        var result = HelmPackageNotesStrategy.ParseChartNotes(yaml, "simple", "0.1.0");

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBe("Simple chart");
        result.Notes.ShouldNotContain("App Version");
    }
}

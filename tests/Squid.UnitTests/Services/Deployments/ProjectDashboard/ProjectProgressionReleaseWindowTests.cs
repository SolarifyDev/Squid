using System.Linq;
using Squid.Core.Services.Deployments.Project;
using Squid.Message.Requests.Deployments.Project;

namespace Squid.UnitTests.Services.Deployments.ProjectDashboard;

/// <summary>
/// Pins the project dashboard (progression) release window: how many recent releases per channel
/// are returned, and how the recent window is merged with the releases that hold a dashboard
/// deployment (current, or previous successful) so every environment column still shows what is
/// deployed there even when that release is far older than the recent window.
/// </summary>
public class ProjectProgressionReleaseWindowTests
{
    [Theory]
    [InlineData(null, 3)]   // not requested → Octopus default of 3 per channel
    [InlineData(0, 3)]      // non-positive is meaningless → default
    [InlineData(-5, 3)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    [InlineData(50, 50)]
    [InlineData(51, 50)]    // clamped so a caller cannot ask the dashboard to load everything
    [InlineData(100000, 50)]
    public void ResolveReleaseHistoryCount_ClampsToSaneWindow(int? requested, int expected)
    {
        ProjectService.ResolveReleaseHistoryCount(requested).ShouldBe(expected);
    }

    [Fact]
    public void ReleaseHistoryCountConstants_Pinned()
    {
        GetProjectProgressionRequest.DefaultReleaseHistoryCount.ShouldBe(3);
        GetProjectProgressionRequest.MaxReleaseHistoryCount.ShouldBe(50);
    }

    [Fact]
    public void SelectDashboardReleaseIds_UnionsRecentAndDashboardReleases_Distinct()
    {
        var result = ProjectService.SelectDashboardReleaseIds(new List<int> { 10, 9, 8 }, new List<int> { 9, 2 }, new List<int> { 1, 2 });

        result.OrderBy(id => id).ShouldBe(new[] { 1, 2, 8, 9, 10 });
    }

    [Fact]
    public void SelectDashboardReleaseIds_NothingDeployed_ReturnsRecentOnly()
    {
        var result = ProjectService.SelectDashboardReleaseIds(new List<int> { 3, 2 }, new List<int>(), new List<int>());

        result.OrderBy(id => id).ShouldBe(new[] { 2, 3 });
    }
}

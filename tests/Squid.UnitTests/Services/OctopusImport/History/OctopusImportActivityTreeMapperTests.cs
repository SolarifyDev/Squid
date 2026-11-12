using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.Deployments;

namespace Squid.UnitTests.Services.OctopusImport.History;

public class OctopusImportActivityTreeMapperTests
{
    private static readonly DateTimeOffset Started = new(2024, 7, 10, 9, 47, 3, TimeSpan.Zero);
    private static readonly DateTimeOffset Ended = new(2024, 7, 10, 9, 48, 11, TimeSpan.Zero);

    [Theory]
    [InlineData("Success", DeploymentActivityLogNodeStatus.Success)]
    [InlineData("SuccessWithWarning", DeploymentActivityLogNodeStatus.Success)]
    [InlineData("Failed", DeploymentActivityLogNodeStatus.Failed)]
    [InlineData("Canceled", DeploymentActivityLogNodeStatus.Failed)]
    [InlineData("Skipped", DeploymentActivityLogNodeStatus.Skipped)]
    [InlineData("Running", DeploymentActivityLogNodeStatus.Running)]
    [InlineData("Pending", DeploymentActivityLogNodeStatus.Pending)]
    [InlineData("Unknown", DeploymentActivityLogNodeStatus.Pending)]
    [InlineData(null, DeploymentActivityLogNodeStatus.Pending)]
    public void MapStatus_Theory(string octopusStatus, DeploymentActivityLogNodeStatus expected)
        => OctopusImportActivityTreeMapper.MapStatus(octopusStatus).ShouldBe(expected);

    [Theory]
    [InlineData("Error", ServerTaskLogCategory.Error)]
    [InlineData("Fatal", ServerTaskLogCategory.Error)]
    [InlineData("Warning", ServerTaskLogCategory.Warning)]
    [InlineData("Info", ServerTaskLogCategory.Info)]
    [InlineData("Verbose", ServerTaskLogCategory.Info)]
    [InlineData("Highlight", ServerTaskLogCategory.Info)]
    [InlineData(null, ServerTaskLogCategory.Info)]
    public void MapCategory_Theory(string octopusCategory, ServerTaskLogCategory expected)
        => OctopusImportActivityTreeMapper.MapCategory(octopusCategory).ShouldBe(expected);

    [Theory]
    [InlineData("Success", DeploymentActivityLogNodeStatus.Success, ServerTaskLogCategory.Info)]
    [InlineData("Failed", DeploymentActivityLogNodeStatus.Failed, ServerTaskLogCategory.Error)]
    [InlineData("TimedOut", DeploymentActivityLogNodeStatus.Failed, ServerTaskLogCategory.Error)]
    [InlineData("Canceled", DeploymentActivityLogNodeStatus.Failed, ServerTaskLogCategory.Warning)]
    [InlineData("Executing", DeploymentActivityLogNodeStatus.Running, ServerTaskLogCategory.Info)]
    [InlineData("Queued", DeploymentActivityLogNodeStatus.Pending, ServerTaskLogCategory.Info)]
    public void SummaryLevel_OneRootWithOneSummaryLine(string octopusState, DeploymentActivityLogNodeStatus rootStatus, ServerTaskLogCategory lineCategory)
    {
        var tree = OctopusImportActivityTreeMapper.Map(Root(octopusState), null, 100);

        var root = tree.Nodes.ShouldHaveSingleItem();
        root.ParentIndex.ShouldBeNull();
        root.NodeType.ShouldBe(DeploymentActivityLogNodeType.Task);
        root.SortOrder.ShouldBe(0);
        root.Status.ShouldBe(rootStatus);
        var line = tree.Lines.ShouldHaveSingleItem();
        line.NodeIndex.ShouldBe(OctopusImportActivityTreeMapper.RootIndex);
        line.Category.ShouldBe(lineCategory);
        line.MessageText.ShouldBe("summary");
    }

    [Fact]
    public void UnrecognisedState_AddsAWarningLine()
    {
        var tree = OctopusImportActivityTreeMapper.Map(Root("Bogus"), null, 100);

        tree.Lines.Count.ShouldBe(2);
        tree.Lines[1].Category.ShouldBe(ServerTaskLogCategory.Warning);
        tree.Lines[1].MessageText.ShouldContain("not recognised");
    }

    [Fact]
    public void DetailLevel_MapsTreeInPreOrder_SingleOctopusRootIsTheTask()
    {
        var details = new OctopusTaskHistoryDetails { ActivityLogs = [OctopusRoot()] };

        var tree = OctopusImportActivityTreeMapper.Map(Root("Success"), details, 100);

        tree.Nodes.Select(n => (n.Name, n.ParentIndex, n.NodeType)).ShouldBe([("Deploy release", null, DeploymentActivityLogNodeType.Task), ("Step 1", 0, DeploymentActivityLogNodeType.Step), ("Action 1", 1, DeploymentActivityLogNodeType.Action), ("Step 2", 0, DeploymentActivityLogNodeType.Step)]);
        tree.Nodes[1].Status.ShouldBe(DeploymentActivityLogNodeStatus.Success);
        tree.Nodes[3].Status.ShouldBe(DeploymentActivityLogNodeStatus.Skipped);
        tree.Nodes.Where(n => n.ParentIndex == 0).Select(n => n.SortOrder).ShouldBe([0, 1]);
        tree.Lines.Select(l => (l.MessageText, l.NodeIndex, l.Category)).ShouldBe([("root line", 0, ServerTaskLogCategory.Info), ("step line", 1, ServerTaskLogCategory.Warning), ("action line", 2, ServerTaskLogCategory.Error)]);
        tree.Lines.Select(l => l.SequenceNumber).ShouldBe([1L, 2L, 3L]);
    }

    [Fact]
    public void PreThreeFourSingleActivityLog_IsTolerated()
    {
        var details = new OctopusTaskHistoryDetails { ActivityLog = OctopusRoot() };

        var tree = OctopusImportActivityTreeMapper.Map(Root("Success"), details, 100);

        tree.Nodes.Count.ShouldBe(4);
        tree.Lines.Count.ShouldBe(3);
    }

    [Fact]
    public void SeveralOctopusRoots_EachBecomesAStepUnderTheTask()
    {
        var details = new OctopusTaskHistoryDetails { ActivityLogs = [new OctopusActivityLogDto { Name = "A" }, new OctopusActivityLogDto { Name = "B" }] };

        var tree = OctopusImportActivityTreeMapper.Map(Root("Success"), details, 100);

        tree.Nodes.Skip(1).ShouldAllBe(n => n.ParentIndex == 0 && n.NodeType == DeploymentActivityLogNodeType.Step);
        tree.Lines.ShouldBeEmpty();
    }

    [Fact]
    public void LogCap_Truncates_AddsWarning()
    {
        var root = new OctopusActivityLogDto { Name = "Task", LogElements = Enumerable.Range(1, 5).Select(i => new OctopusLogElementDto { Category = "Info", MessageText = $"line {i}" }).ToList() };

        var tree = OctopusImportActivityTreeMapper.Map(Root("Success"), new OctopusTaskHistoryDetails { ActivityLogs = [root] }, 3);

        tree.Lines.Count.ShouldBe(4);
        tree.Lines.Take(3).Select(l => l.MessageText).ShouldBe(["line 1", "line 2", "line 3"]);
        tree.Lines[3].Category.ShouldBe(ServerTaskLogCategory.Warning);
        tree.Lines[3].MessageText.ShouldBe("2 more Octopus log line(s) were not imported (limit 3 per task).");
        tree.Lines.Select(l => l.SequenceNumber).ShouldBe([1L, 2L, 3L, 4L]);
    }

    [Fact]
    public void LogCap_NotReached_AddsNoWarning()
    {
        var tree = OctopusImportActivityTreeMapper.Map(Root("Success"), new OctopusTaskHistoryDetails { ActivityLogs = [OctopusRoot()] }, 3);

        tree.Lines.ShouldNotContain(l => l.Category == ServerTaskLogCategory.Warning && l.MessageText.Contains("not imported"));
    }

    [Fact]
    public void MaxLogLinesEnvVar_ConstantNamePinned()
    {
        // Renaming this constant breaks every operator who tuned the cap through the environment. Hard-pin it.
        OctopusImportDeploymentHistoryImporter.MaxLogLinesPerTaskEnvVar.ShouldBe("SQUID_OCTOPUS_IMPORT_MAX_LOG_LINES_PER_TASK");
    }

    [Theory]
    [InlineData(null, 20000)]
    [InlineData("", 20000)]
    [InlineData("abc", 20000)]
    [InlineData("0", 20000)]
    [InlineData("-5", 20000)]
    [InlineData("150", 150)]
    public void MaxLogLinesPerTask_ReadsTheEnvironment(string value, int expected)
    {
        var previous = System.Environment.GetEnvironmentVariable(OctopusImportDeploymentHistoryImporter.MaxLogLinesPerTaskEnvVar);

        try
        {
            System.Environment.SetEnvironmentVariable(OctopusImportDeploymentHistoryImporter.MaxLogLinesPerTaskEnvVar, value);

            OctopusImportDeploymentHistoryImporter.MaxLogLinesPerTask().ShouldBe(expected);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(OctopusImportDeploymentHistoryImporter.MaxLogLinesPerTaskEnvVar, previous);
        }
    }

    private static OctopusImportActivityRoot Root(string octopusState)
        => new("Deploy release", OctopusImportTaskStateNormalizer.Normalize(octopusState), Started, Ended, "summary", "{}");

    private static OctopusActivityLogDto OctopusRoot()
        => new()
        {
            Name = "ServerTasks-1",
            LogElements = [new OctopusLogElementDto { Category = "Info", MessageText = "root line" }],
            Children =
            [
                new OctopusActivityLogDto
                {
                    Name = "Step 1", Status = "Success",
                    LogElements = [new OctopusLogElementDto { Category = "Warning", MessageText = "step line" }],
                    Children = [new OctopusActivityLogDto { Name = "Action 1", Status = "Failed", LogElements = [new OctopusLogElementDto { Category = "Error", MessageText = "action line" }] }]
                },
                new OctopusActivityLogDto { Name = "Step 2", Status = "Skipped" }
            ]
        };
}

using System.IO;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.UnitTests.Services.OctopusImport.History;

public class OctopusImportHistoryDetailsStoreTests : IDisposable
{
    private readonly string _sessionDirectory = Path.Combine(Path.GetTempPath(), $"squid-history-details-{Guid.NewGuid():N}");

    [Fact]
    public async Task WriteThenRead_RoundTripsTheDetails()
    {
        var store = new OctopusImportHistoryDetailsStore();
        var details = new OctopusTaskHistoryDetails { ActivityLogs = [new OctopusActivityLogDto { Name = "Task", LogElements = [new OctopusLogElementDto { MessageText = "hello" }] }], Artifacts = [new OctopusArtifactDto { Filename = "a.txt" }] };

        await store.WriteAsync(_sessionDirectory, "ServerTasks-1", details);
        var read = await store.ReadAsync(_sessionDirectory, "servertasks-1 ");

        read.RootActivities.ShouldHaveSingleItem().LogElements.ShouldHaveSingleItem().MessageText.ShouldBe("hello");
        read.Artifacts.ShouldHaveSingleItem().Filename.ShouldBe("a.txt");
    }

    // The fetch caps stored details by what each write adds: a task written again (a retried chunk) adds only its change in size.
    [Fact]
    public async Task Write_ReturnsWhatTheStoreGrewBy()
    {
        var store = new OctopusImportHistoryDetailsStore();
        var details = new OctopusTaskHistoryDetails { Artifacts = [new OctopusArtifactDto { Filename = "a.txt" }] };

        var first = await store.WriteAsync(_sessionDirectory, "ServerTasks-1", details);
        var again = await store.WriteAsync(_sessionDirectory, "ServerTasks-1", details);

        first.ShouldBe(new FileInfo(OctopusImportHistoryDetailsStore.PathFor(_sessionDirectory, "ServerTasks-1")).Length);
        again.ShouldBe(0);
    }

    // A rewrite cut off part way (the chunk deadline, a full disk) must not leave a truncated file the import later fails to read:
    // the details written before stay readable until the new ones are complete.
    [Fact]
    public async Task RewriteCutOff_PreviousDetailsStayReadable()
    {
        var store = new OctopusImportHistoryDetailsStore();
        await store.WriteAsync(_sessionDirectory, "ServerTasks-1", new OctopusTaskHistoryDetails { Artifacts = [new OctopusArtifactDto { Filename = "a.txt" }] });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => store.WriteAsync(_sessionDirectory, "ServerTasks-1", new OctopusTaskHistoryDetails { Artifacts = [new OctopusArtifactDto { Filename = "b.txt" }] }, cancelled.Token));

        (await store.ReadAsync(_sessionDirectory, "ServerTasks-1")).Artifacts.ShouldHaveSingleItem().Filename.ShouldBe("a.txt");
        Directory.GetFiles(Path.Combine(_sessionDirectory, OctopusImportHistoryDetailsStore.DirectoryName)).ShouldHaveSingleItem("no partial file is left behind");
    }

    [Theory]
    [InlineData(null, "ServerTasks-1")]
    [InlineData("", "ServerTasks-1")]
    [InlineData("dir", null)]
    [InlineData("dir", "ServerTasks-404")]
    public async Task Read_WhenNothingWasStored_ReturnsNull(string sessionDirectory, string taskId)
        => (await new OctopusImportHistoryDetailsStore().ReadAsync(sessionDirectory == "dir" ? _sessionDirectory : sessionDirectory, taskId)).ShouldBeNull();

    [Fact]
    public void PathFor_NeverUsesTheRawTaskId()
    {
        var path = OctopusImportHistoryDetailsStore.PathFor(_sessionDirectory, "../../etc/passwd");

        Path.GetDirectoryName(path).ShouldBe(Path.Combine(_sessionDirectory, OctopusImportHistoryDetailsStore.DirectoryName));
        Path.GetFileName(path).ShouldMatch("^[0-9a-f]{64}\\.json\\.gz$");
    }

    public void Dispose()
    {
        if (Directory.Exists(_sessionDirectory)) Directory.Delete(_sessionDirectory, recursive: true);
    }
}

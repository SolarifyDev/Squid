using System.IO;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SessionFiles;
using Squid.UnitTests.Services.OctopusImport.SessionFiles;

namespace Squid.UnitTests.Services.OctopusImport.History;

public class OctopusImportHistoryDetailsStoreTests
{
    private readonly InMemoryOctopusImportSessionFileStore _files = new();
    private readonly Guid _sessionId = Guid.NewGuid();

    [Fact]
    public async Task WriteThenRead_RoundTripsTheDetails()
    {
        var store = new OctopusImportHistoryDetailsStore(_files);
        var details = new OctopusTaskHistoryDetails { ActivityLogs = [new OctopusActivityLogDto { Name = "Task", LogElements = [new OctopusLogElementDto { MessageText = "hello" }] }], Artifacts = [new OctopusArtifactDto { Filename = "a.txt" }] };

        await store.WriteAsync(_sessionId, "ServerTasks-1", details);
        var read = await store.ReadAsync(_sessionId, "servertasks-1 ");

        read.RootActivities.ShouldHaveSingleItem().LogElements.ShouldHaveSingleItem().MessageText.ShouldBe("hello");
        read.Artifacts.ShouldHaveSingleItem().Filename.ShouldBe("a.txt");
    }

    // The fetch caps stored details by what each write adds: a task written again (a retried chunk) adds only its change in size,
    // and is not a newly stored task.
    [Fact]
    public async Task Write_ReportsWhatTheStoreGrewBy_AndWhetherTheTaskIsNew()
    {
        var store = new OctopusImportHistoryDetailsStore(_files);
        var details = new OctopusTaskHistoryDetails { Artifacts = [new OctopusArtifactDto { Filename = "a.txt" }] };

        var first = await store.WriteAsync(_sessionId, "ServerTasks-1", details);
        var again = await store.WriteAsync(_sessionId, "ServerTasks-1", details);

        (first.Growth, first.Created).ShouldBe(((await _files.ReadAsync(_sessionId, OctopusImportHistoryDetailsStore.PathFor("ServerTasks-1"))).LongLength, true));
        (again.Growth, again.Created).ShouldBe((0L, false));
        (await store.CountAsync(_sessionId)).ShouldBe(1);
    }

    [Theory]
    [InlineData(false, "ServerTasks-1")]
    [InlineData(true, null)]
    [InlineData(true, "ServerTasks-404")]
    public async Task Read_WhenNothingWasStored_ReturnsNull(bool session, string taskId)
        => (await new OctopusImportHistoryDetailsStore(_files).ReadAsync(session ? _sessionId : Guid.Empty, taskId)).ShouldBeNull();

    [Fact]
    public void PathFor_NeverUsesTheRawTaskId()
    {
        var path = OctopusImportHistoryDetailsStore.PathFor("../../etc/passwd");

        path.ShouldStartWith(OctopusImportSessionFilePaths.DetailsPrefix);
        path[OctopusImportSessionFilePaths.DetailsPrefix.Length..].ShouldMatch("^[0-9a-f]{64}\\.json\\.gz$");
    }
}

using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportProjectSelectionServiceTests
{
    private const int SpaceId = 7;
    private const string Suffix = "00112233445566778899AABBCCDDEEFF";

    // This server's copy of the session's source, never the path registered by whichever server took the upload.
    private const string Materialized = "/tmp/squid-selection/this-server/export.zip";

    private readonly Mock<IOctopusRestProjectLister> _lister = new();
    private readonly Mock<IOctopusImportPlanningPipeline> _pipeline = new();
    private readonly Mock<IOctopusImportSourceMapDataProvider> _sourceMap = new();
    private readonly Mock<IOctopusImportSessionDataProvider> _sessions = new();
    private readonly Mock<IOctopusImportSessionService> _sessionService = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IOctopusImportTemporaryUploadStore> _uploadStore = new();
    private readonly OctopusImportProjectSelectionService _service;

    public OctopusImportProjectSelectionServiceTests()
    {
        _currentUser.SetupGet(u => u.Id).Returns(42);
        _uploadStore.Setup(u => u.MaterializeAsync(It.IsAny<OctopusImportSession>(), It.IsAny<CancellationToken>())).ReturnsAsync(Materialized);
        _sourceMap.Setup(s => s.LoadAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(Prior(("Project", "Projects-1")));
        _sessionService.Setup(s => s.UpdateProjectSelectionAsync(It.IsAny<Guid>(), SpaceId, It.IsAny<List<string>>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto());
        _service = new OctopusImportProjectSelectionService(_lister.Object, _pipeline.Object, _sourceMap.Object, _sessions.Object, _sessionService.Object, _currentUser.Object, _uploadStore.Object);
    }

    [Fact]
    public void MaxSelectedProjects_Pinned() => OctopusImportProjectSelectionService.MaxSelectedProjects.ShouldBe(5000);

    // ── Well-formed ────────────────────────────────────────────────────────

    public static IEnumerable<object[]> Malformed()
    {
        yield return [new List<string>(), OctopusImportProjectSelectionProblem.Empty];
        yield return [Enumerable.Range(1, 5001).Select(i => $"Projects-{i}").ToList(), OctopusImportProjectSelectionProblem.TooMany];
        yield return [new List<string> { "Projects-1", "../admin" }, OctopusImportProjectSelectionProblem.InvalidId];
        yield return [new List<string> { "Projects 1" }, OctopusImportProjectSelectionProblem.InvalidId];
        yield return [new List<string> { "" }, OctopusImportProjectSelectionProblem.InvalidId];
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task Malformed_Refused_NothingStored(List<string> ids, OctopusImportProjectSelectionProblem problem)
    {
        var session = Session(OctopusImportSessionState.Extracted);

        var ex = await Should.ThrowAsync<OctopusImportProjectSelectionInvalidException>(() => _service.SelectProjectsAsync(session.SessionId, SpaceId, ids));

        ex.Problem.ShouldBe(problem);
        _sessionService.Verify(s => s.UpdateProjectSelectionAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<List<string>>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpToTheLimit_Accepted()
    {
        var session = Session(OctopusImportSessionState.Extracted);

        await _service.SelectProjectsAsync(session.SessionId, SpaceId, Enumerable.Range(1, 5000).Select(i => $"Projects-{i}").ToList());

        _sessionService.Verify(s => s.UpdateProjectSelectionAsync(session.SessionId, SpaceId, It.Is<List<string>>(l => l.Count == 5000), session.DataVersion, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Stored trimmed, once (ignoring case) and in order, under the data version it was checked with; null is the whole space.
    [Theory]
    [InlineData(new[] { " Projects-2 ", "projects-2", "Projects-1" }, new[] { "Projects-1", "Projects-2" })]
    [InlineData(null, null)]
    public async Task Selection_StoredNormalised_UnderTheReadDataVersion(string[] posted, string[] stored)
    {
        var session = Session(OctopusImportSessionState.Previewed);

        await _service.SelectProjectsAsync(session.SessionId, SpaceId, posted);

        _sessionService.Verify(s => s.UpdateProjectSelectionAsync(session.SessionId, SpaceId, It.Is<List<string>>(l => stored == null ? l == null : l.SequenceEqual(stored)), session.DataVersion, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Session state ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(OctopusImportSessionState.Importing)]
    [InlineData(OctopusImportSessionState.Succeeded)]
    [InlineData(OctopusImportSessionState.Failed)]
    [InlineData(OctopusImportSessionState.Expired)]
    public async Task SettledOrImportingSession_Refused(OctopusImportSessionState state)
    {
        var session = Session(state);

        await Should.ThrowAsync<OctopusImportSessionStateTransitionException>(() => _service.SelectProjectsAsync(session.SessionId, SpaceId, ["Projects-1"]));
    }

    [Fact]
    public async Task AnotherUsersSession_NotFound()
        => await Should.ThrowAsync<OctopusImportSessionNotFoundException>(() => _service.SelectProjectsAsync(Guid.NewGuid(), SpaceId, ["Projects-1"]));

    [Fact]
    public async Task ChunkHoldsTheLease_Refused()
    {
        var session = Session(OctopusImportSessionState.Uploaded, new OctopusImportHistoryFetchDto { StartedAt = DateTimeOffset.UtcNow, LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1) });

        await Should.ThrowAsync<OctopusImportSessionConcurrencyException>(() => _service.SelectProjectsAsync(session.SessionId, SpaceId, ["Projects-1"]));
    }

    // ── Within what a started read reads ───────────────────────────────────

    [Theory]
    [InlineData(new[] { "Projects-1" }, true)]
    [InlineData(new[] { "projects-2", "Projects-1" }, true)]
    [InlineData(new[] { "Projects-1", "Projects-3" }, false)]
    [InlineData(null, false)]
    public async Task ReadStarted_OnlyPartOfTheChosenProjects(string[] posted, bool accepted)
    {
        var session = Session(OctopusImportSessionState.Uploaded, new OctopusImportHistoryFetchDto { StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, ["Projects-1", "Projects-2"]);

        var select = () => _service.SelectProjectsAsync(session.SessionId, SpaceId, posted);

        if (accepted) await select();
        else await Should.ThrowAsync<OctopusImportProjectSelectionBeyondReadException>(select);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, new[] { "Projects-1" })]
    public async Task ReadNotStartedOrReadingTheWholeSpace_AnySelection(bool started, string[] current)
    {
        var session = Session(OctopusImportSessionState.Uploaded, new OctopusImportHistoryFetchDto { StartedAt = started ? DateTimeOffset.UtcNow : null }, current?.ToList());

        await _service.SelectProjectsAsync(session.SessionId, SpaceId, ["Projects-9"]);

        _sessionService.Verify(s => s.UpdateProjectSelectionAsync(session.SessionId, SpaceId, It.IsAny<List<string>>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // An export file's suffixed ids and the bare ids compare as one.
    [Fact]
    public void WithinRead_ComparesWithoutTheExportSuffix()
        => OctopusImportProjectSelectionService.EnsureWithinRead(new OctopusImportSourceSummaryDto { HistoryFetch = new OctopusImportHistoryFetchDto { StartedAt = DateTimeOffset.UtcNow }, SelectedProjectIds = [$"Projects-1-{Suffix}"] }, ["Projects-1"], Guid.NewGuid());

    // ── Listing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ServerListing_SortedByName_MarksWhatThisSpaceImportedBefore_NoSelectionYet()
    {
        _lister.Setup(l => l.ListProjectsAsync(It.IsAny<OctopusServerConnection>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusRestProjectListing("inst-1", "Spaces-1", [new("ProjectGroups-2", "Zeta"), new("ProjectGroups-1", "Alpha")], [new("Projects-2", "Web", "ProjectGroups-1", false), new("Projects-1", "Api", "ProjectGroups-1", true)]));

        var list = await _service.ListServerProjectsAsync(SpaceId, new OctopusServerConnection("https://octopus.example.com", "API-KEY", null));

        list.Groups.Select(g => g.Name).ShouldBe(["Alpha", "Zeta"]);
        list.Projects.Select(p => (p.Id, p.Name, p.IsDisabled, p.PreviouslyImported)).ShouldBe([("Projects-1", "Api", true, true), ("Projects-2", "Web", false, false)]);
        list.SelectedProjectIds.ShouldBeNull();
    }

    // Octopus ids are sequential per installation: rows another installation recorded say nothing about this server's projects.
    [Theory]
    [InlineData(null, true)]
    [InlineData("inst-1", true)]
    [InlineData("INST-1", true)]
    [InlineData("inst-2", false)]
    public async Task ServerListing_ImportedBefore_OnlyWhenThisSpaceRecordedTheSameInstallation(string recordedInstallationId, bool previouslyImported)
    {
        _sourceMap.Setup(m => m.LoadAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportPriorMappings(new Dictionary<(string Type, string Id), OctopusImportPriorMapping> { [("Project", "Projects-1")] = new(500, OctopusImportSourceMapQueries.CreatedOutcome, null, null) }, recordedInstallationId));
        _lister.Setup(l => l.ListProjectsAsync(It.IsAny<OctopusServerConnection>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusRestProjectListing("inst-1", "Spaces-1", [], [new("Projects-1", "Api", null, false)]));

        var list = await _service.ListServerProjectsAsync(SpaceId, new OctopusServerConnection("https://octopus.example.com", "API-KEY", null));

        list.Projects.Single().PreviouslyImported.ShouldBe(previouslyImported);
    }

    [Fact]
    public async Task SessionListing_ReadsTheSourceGraph_MarksImportedAcrossTheExportSuffix_ReturnsTheSelection()
    {
        var session = Session(OctopusImportSessionState.Previewed, selected: [$"Projects-2-{Suffix}"]);
        _pipeline.Setup(p => p.BuildGraphAsync(Materialized, It.IsAny<CancellationToken>())).ReturnsAsync(await new OctopusImportScenario().Project($"Projects-1-{Suffix}", "Api", [], []).Project($"Projects-2-{Suffix}", "Web", [], []).BuildGraphAsync());

        var list = await _service.ListSessionProjectsAsync(session.SessionId, SpaceId);

        list.Projects.Select(p => (p.Id, p.PreviouslyImported)).ShouldBe([($"Projects-1-{Suffix}", true), ($"Projects-2-{Suffix}", false)]);
        list.SelectedProjectIds.ShouldBe([$"Projects-2-{Suffix}"]);
    }

    // The picker marks a Config-as-Code project, from the server listing and, after a reload, from the session's source.
    [Fact]
    public async Task ServerListing_ConfigAsCodeFlagCarried()
    {
        _lister.Setup(l => l.ListProjectsAsync(It.IsAny<OctopusServerConnection>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusRestProjectListing("inst-1", "Spaces-1", [], [new("Projects-1", "Api", null, false), new("Projects-2", "Git", null, false, IsConfigAsCode: true)]));

        var list = await _service.ListServerProjectsAsync(SpaceId, new OctopusServerConnection("https://octopus.example.com", "API-KEY", null));

        list.Projects.Select(p => (p.Id, p.IsConfigAsCode)).ShouldBe([("Projects-1", false), ("Projects-2", true)]);
    }

    [Fact]
    public async Task SessionListing_ConfigAsCodeFlaggedFromTheSourceGraph()
    {
        var session = Session(OctopusImportSessionState.Extracted);
        _pipeline.Setup(p => p.BuildGraphAsync(Materialized, It.IsAny<CancellationToken>())).ReturnsAsync(await new OctopusImportScenario().Project("Projects-1", "Api", [], []).ConfigAsCodeProject("Projects-2", "Git", null, []).BuildGraphAsync());

        var list = await _service.ListSessionProjectsAsync(session.SessionId, SpaceId);

        list.Projects.Select(p => (p.Id, p.IsConfigAsCode)).ShouldBe([("Projects-1", false), ("Projects-2", true)]);
    }

    // Old clients and "select all" still send it: accepted, and the preview skips it with its reason.
    [Fact]
    public async Task Select_ConfigAsCodeId_Accepted()
    {
        var session = Session(OctopusImportSessionState.Extracted);

        await _service.SelectProjectsAsync(session.SessionId, SpaceId, ["Projects-1", "Projects-2"]);

        _sessionService.Verify(s => s.UpdateProjectSelectionAsync(session.SessionId, SpaceId, It.Is<List<string>>(ids => ids.SequenceEqual(new[] { "Projects-1", "Projects-2" })), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SessionListing_BeforeTheSourceIsRead_Refused()
    {
        var session = Session(OctopusImportSessionState.Uploaded);

        await Should.ThrowAsync<OctopusImportSessionStateTransitionException>(() => _service.ListSessionProjectsAsync(session.SessionId, SpaceId));
    }

    // The session exists but neither this server nor the database holds its source any more: the listing says so instead of reading nothing.
    [Fact]
    public async Task SessionListing_SourceGone_SessionNotFound()
    {
        var session = Session(OctopusImportSessionState.Extracted);
        _uploadStore.Setup(u => u.MaterializeAsync(It.Is<OctopusImportSession>(s => s.SessionId == session.SessionId), It.IsAny<CancellationToken>())).ReturnsAsync((string)null);

        await Should.ThrowAsync<OctopusImportSessionNotFoundException>(() => _service.ListSessionProjectsAsync(session.SessionId, SpaceId));

        _pipeline.Verify(p => p.BuildGraphAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private OctopusImportSession Session(OctopusImportSessionState state, OctopusImportHistoryFetchDto historyFetch = null, List<string> selected = null)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
        var session = new OctopusImportSession { SessionId = Guid.NewGuid(), OwnerUserId = 42, DestinationSpaceId = SpaceId, State = state.ToString(), TemporaryUploadPath = "/tmp/squid-selection/export.zip", DataVersion = Guid.NewGuid().ToByteArray(), SourceSummaryJson = JsonSerializer.Serialize(new OctopusImportSourceSummaryDto { HistoryFetch = historyFetch, SelectedProjectIds = selected }, options) };

        _sessions.Setup(p => p.GetSessionNoTrackingAsync(session.SessionId, 42, SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        return session;
    }

    private static OctopusImportPriorMappings Prior(params (string Type, string Id)[] rows)
        => new(rows.ToDictionary(r => r, r => new OctopusImportPriorMapping(500, OctopusImportSourceMapQueries.CreatedOutcome, null, null)), null);
}

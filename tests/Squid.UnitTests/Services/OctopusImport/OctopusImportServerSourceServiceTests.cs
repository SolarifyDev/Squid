using System.IO;
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

public sealed class OctopusImportServerSourceServiceTests : IDisposable
{
    private readonly List<string> _directories = [];
    private const string ServerUrl = "https://octopus.example.com/";

    private readonly Mock<IOctopusImportSessionDataProvider> _dataProvider = new();
    private readonly Mock<IOctopusImportSessionService> _sessionService = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IOctopusImportTemporaryUploadStore> _uploadStore = new();
    private readonly Mock<IOctopusRestHistoryFetcher> _fetcher = new();
    private readonly Mock<IOctopusImportSourceMapDataProvider> _sourceMap = new();
    private readonly List<(OctopusImportHistoryFetchDto Saved, Guid? Expected)> _updates = [];
    private readonly OctopusImportServerSourceService _service;

    public OctopusImportServerSourceServiceTests()
    {
        _currentUser.SetupGet(u => u.Id).Returns(42);
        _sourceMap.Setup(s => s.LoadAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(OctopusImportPriorMappings.Empty);
        _sessionService.Setup(s => s.UpdateHistoryFetchAsync(It.IsAny<Guid>(), 7, It.IsAny<OctopusImportHistoryFetchDto>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, int _, OctopusImportHistoryFetchDto dto, Guid? expected, CancellationToken _) => _updates.Add((Snapshot(dto), expected)))
            .ReturnsAsync(new OctopusImportSessionDto());
        _fetcher.Setup(f => f.FetchChunkAsync(It.IsAny<OctopusHistoryFetchContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusHistoryFetchProgressDto());
        _service = new OctopusImportServerSourceService(_dataProvider.Object, _sessionService.Object, _currentUser.Object, _uploadStore.Object, _fetcher.Object, _sourceMap.Object, new OctopusRestOptions { ChunkDuration = TimeSpan.FromSeconds(20) });
    }

    [Theory]
    [InlineData("https://other.example.com/", "Spaces-1", "server")]
    [InlineData("https://octopus.example.com:8443/", "Spaces-1", "server")]
    [InlineData(ServerUrl, "Spaces-2", "space")]
    public async Task FetchHandler_ServerPinMismatch_Rejected(string serverUrl, string octopusSpaceId, string pinnedField)
    {
        var session = Session(new OctopusImportHistoryFetchDto { ServerHost = "octopus.example.com", OctopusSpaceId = "Spaces-1", ChunkId = Guid.NewGuid() });

        var ex = await Should.ThrowAsync<OctopusImportHistoryServerMismatchException>(() => _service.FetchHistoryAsync(Request(session, serverUrl, octopusSpaceId)));

        ex.PinnedField.ShouldBe(pinnedField);
        _updates.ShouldBeEmpty();
        _fetcher.Verify(f => f.FetchChunkAsync(It.IsAny<OctopusHistoryFetchContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("https://OCTOPUS.example.com/octopus/", "spaces-1")]
    [InlineData(ServerUrl, null)]
    public async Task FetchHistory_SamePins_RunsTheChunk(string serverUrl, string octopusSpaceId)
    {
        var session = Session(new OctopusImportHistoryFetchDto { ServerHost = "octopus.example.com", OctopusSpaceId = "Spaces-1", ProjectIds = ["Projects-1"] });

        await _service.FetchHistoryAsync(Request(session, serverUrl, octopusSpaceId));

        _fetcher.Verify(f => f.FetchChunkAsync(It.Is<OctopusHistoryFetchContext>(c => c.Cursor.OctopusSpaceId == "Spaces-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FetchHistory_FirstChunk_PinsServerAndSpace_ClaimsRunsAndReleases()
    {
        var session = Session(null);
        OctopusHistoryFetchContext seen = null;
        _fetcher.Setup(f => f.FetchChunkAsync(It.IsAny<OctopusHistoryFetchContext>(), It.IsAny<CancellationToken>())).Callback((OctopusHistoryFetchContext c, CancellationToken _) => seen = c).ReturnsAsync(new OctopusHistoryFetchProgressDto { ProjectIndex = 1 });

        var progress = await _service.FetchHistoryAsync(Request(session, "https://Octopus.Example.com:443/", "Spaces-1"));

        progress.ProjectIndex.ShouldBe(1);
        _updates.Count.ShouldBe(2);
        var (claimed, claimExpected) = _updates[0];
        claimExpected.ShouldBeNull();
        claimed.ChunkId.ShouldNotBeNull();
        claimed.LeaseExpiresAt.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(20));
        claimed.ServerHost.ShouldBe("octopus.example.com");
        claimed.OctopusSpaceId.ShouldBe("Spaces-1");
        var (released, releaseExpected) = _updates[1];
        releaseExpected.ShouldBe(claimed.ChunkId);
        released.LeaseExpiresAt.ShouldBeNull();
        seen.ApiKey.ShouldBe("API-KEY");
        seen.SessionDirectory.ShouldBe(Path.GetDirectoryName(Path.GetFullPath(session.TemporaryUploadPath)));
    }

    [Fact]
    public async Task FetchHistory_ActiveLease_Throws409_WithoutClaimingOrFetching()
    {
        var session = Session(new OctopusImportHistoryFetchDto { ChunkId = Guid.NewGuid(), LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1) });

        await Should.ThrowAsync<OctopusImportSessionConcurrencyException>(() => _service.FetchHistoryAsync(Request(session)));

        _updates.ShouldBeEmpty();
        _fetcher.Verify(f => f.FetchChunkAsync(It.IsAny<OctopusHistoryFetchContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FetchHistory_ExpiredLease_IsTakenOver()
    {
        var stale = Guid.NewGuid();
        var session = Session(new OctopusImportHistoryFetchDto { ChunkId = stale, LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), ProjectIds = ["Projects-1"] });

        await _service.FetchHistoryAsync(Request(session));

        _updates[0].Expected.ShouldBe(stale);
        _updates[0].Saved.ChunkId.ShouldNotBe(stale);
    }

    [Fact]
    public async Task FetchHistory_ChunkFails_ReleasesTheLeaseAndRethrows()
    {
        var session = Session(new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"] });
        _fetcher.Setup(f => f.FetchChunkAsync(It.IsAny<OctopusHistoryFetchContext>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OctopusRestException(OctopusRestErrorKind.Unauthorized));

        var ex = await Should.ThrowAsync<OctopusRestException>(() => _service.FetchHistoryAsync(Request(session)));

        ex.Kind.ShouldBe(OctopusRestErrorKind.Unauthorized);
        _updates.Count.ShouldBe(2);
        _updates[1].Saved.LeaseExpiresAt.ShouldBeNull();
        _updates[1].Expected.ShouldBe(_updates[0].Saved.ChunkId);
    }

    [Fact]
    public async Task FetchHistory_AlreadyDone_ReturnsProgressWithoutTouchingTheServer()
    {
        var session = Session(new OctopusImportHistoryFetchDto { Done = true, Phase = OctopusHistoryFetchPhase.Done, ProjectIds = ["Projects-1"], ProjectIndex = 1 });

        var progress = await _service.FetchHistoryAsync(Request(session));

        progress.Done.ShouldBeTrue();
        progress.ProjectCount.ShouldBe(1);
        _updates.ShouldBeEmpty();
        _fetcher.Verify(f => f.FetchChunkAsync(It.IsAny<OctopusHistoryFetchContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(OctopusImportSessionState.Extracted)]
    [InlineData(OctopusImportSessionState.Failed)]
    public async Task FetchHistory_SessionPastUploaded_Rejected(OctopusImportSessionState state)
    {
        var session = Session(null, state);

        await Should.ThrowAsync<OctopusImportSessionStateTransitionException>(() => _service.FetchHistoryAsync(Request(session)));

        _updates.ShouldBeEmpty();
    }

    // D1: one source per session. A session opened from an export file never reads from a server, whatever it is asked.
    [Theory]
    [InlineData("Zip")]
    [InlineData("Json")]
    [InlineData(null)]
    public async Task FetchHistory_SessionOpenedFromAFile_RefusedBeforeAnyRequest(string detectedFormat)
    {
        var session = Session(null, mainFile: "export.zip", detectedFormat: detectedFormat);

        var ex = await Should.ThrowAsync<OctopusImportSessionNotServerSourceException>(() => _service.FetchHistoryAsync(Request(session)));

        ex.SessionId.ShouldBe(session.SessionId);
        _updates.ShouldBeEmpty();
        _fetcher.Verify(f => f.FetchChunkAsync(It.IsAny<OctopusHistoryFetchContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A server session's cursor always exists; a missing one still starts with the space's catalog, never with history.
    [Fact]
    public async Task FetchHistory_ServerSessionWithoutACursor_StartsWithTheCatalog()
    {
        var session = Session(null);

        await _service.FetchHistoryAsync(Request(session));

        _fetcher.Verify(f => f.FetchChunkAsync(It.Is<OctopusHistoryFetchContext>(c => c.Cursor.Phase == OctopusHistoryFetchPhase.Catalog), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Connect_CreatesAServerSession_RegisteredAtTheBundlePath()
    {
        var sessionId = Guid.NewGuid();
        var directory = Path.Combine(Path.GetTempPath(), $"squid-connect-{sessionId:N}");
        OctopusImportSourceSummaryDto summary = null;
        _sessionService.Setup(s => s.CreateSessionAsync(7, It.IsAny<OctopusImportSourceSummaryDto>(), It.IsAny<CancellationToken>())).Callback((int _, OctopusImportSourceSummaryDto s, CancellationToken _) => summary = s).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId });
        _uploadStore.Setup(s => s.GetSessionDirectory(sessionId)).Returns(directory);
        _sessionService.Setup(s => s.RegisterTemporaryUploadAsync(sessionId, 7, It.IsAny<OctopusImportTemporaryUpload>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportSessionDto { SessionId = sessionId, State = OctopusImportSessionState.Uploaded });

        try
        {
            var session = await _service.ConnectAsync(7);

            session.State.ShouldBe(OctopusImportSessionState.Uploaded);
            summary.DetectedFormat.ShouldBe(OctopusImportServerSourceService.ServerSourceFormat);
            summary.HistoryFetch.ShouldNotBeNull().Done.ShouldBeFalse();
            summary.HistoryFetch.Phase.ShouldBe(OctopusHistoryFetchPhase.Catalog, "a server session reads the whole space, starting with its catalog");
            Directory.Exists(directory).ShouldBeTrue();
            _sessionService.Verify(s => s.RegisterTemporaryUploadAsync(sessionId, 7, It.Is<OctopusImportTemporaryUpload>(u => u.Path == Path.Combine(directory, OctopusImportSourceReader.HistoryBundleFileName) && u.SizeBytes == 0), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public void Dispose()
    {
        foreach (var directory in _directories) Directory.Delete(directory, recursive: true);
    }

    private OctopusImportSession Session(OctopusImportHistoryFetchDto historyFetch, OctopusImportSessionState state = OctopusImportSessionState.Uploaded, string mainFile = OctopusImportSourceReader.HistoryBundleFileName, string detectedFormat = OctopusImportServerSourceService.ServerSourceFormat)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"squid-server-source-{Guid.NewGuid():N}")).FullName;
        _directories.Add(directory);
        var mainPath = Path.Combine(directory, mainFile);

        if (mainFile != OctopusImportSourceReader.HistoryBundleFileName) File.WriteAllText(mainPath, "{}");

        var session = new OctopusImportSession { SessionId = Guid.NewGuid(), OwnerUserId = 42, DestinationSpaceId = 7, State = state.ToString(), TemporaryUploadPath = mainPath, SourceSummaryJson = JsonSerializer.Serialize(new OctopusImportSourceSummaryDto { DetectedFormat = detectedFormat, HistoryFetch = historyFetch }, options) };

        _dataProvider.Setup(p => p.GetSessionNoTrackingAsync(session.SessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        return session;
    }

    private static OctopusHistoryFetchRequest Request(OctopusImportSession session, string serverUrl = ServerUrl, string octopusSpaceId = "Spaces-1") => new(session.SessionId, 7, serverUrl, "API-KEY", octopusSpaceId);

    private static OctopusImportHistoryFetchDto Snapshot(OctopusImportHistoryFetchDto dto) => JsonSerializer.Deserialize<OctopusImportHistoryFetchDto>(JsonSerializer.Serialize(dto));
}

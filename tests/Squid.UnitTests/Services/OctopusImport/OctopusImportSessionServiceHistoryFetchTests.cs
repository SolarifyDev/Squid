using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportSessionServiceHistoryFetchTests
{
    private readonly Mock<IOctopusImportSessionDataProvider> _dataProvider = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly OctopusImportSessionService _service;

    public OctopusImportSessionServiceHistoryFetchTests()
    {
        var settings = new Mock<IOctopusImportTemporaryUploadSettings>();
        settings.SetupGet(s => s.DefaultRetentionPeriod).Returns(TimeSpan.FromHours(24));
        _currentUser.SetupGet(u => u.Id).Returns(42);
        _service = new OctopusImportSessionService(_dataProvider.Object, _currentUser.Object, settings.Object);
    }

    [Fact]
    public async Task UpdateHistoryFetch_ExpectedChunkMatches_StoresTheCursorUnderTheDataVersionGuard()
    {
        var previous = Guid.NewGuid();
        var session = Session(OctopusImportSessionState.Uploaded, new OctopusImportHistoryFetchDto { ChunkId = previous });
        var dataVersion = session.DataVersion;

        var dto = await _service.UpdateHistoryFetchAsync(session.SessionId, 7, new OctopusImportHistoryFetchDto { ChunkId = Guid.NewGuid(), ProjectIds = ["Projects-1"], ProjectIndex = 1, Phase = OctopusHistoryFetchPhase.Deployments, ServerHost = "octopus.example.com" }, previous);

        dto.SourceSummary.HistoryFetch.ProjectIndex.ShouldBe(1);
        dto.SourceSummary.HistoryFetch.Phase.ShouldBe(OctopusHistoryFetchPhase.Deployments);
        dto.SourceSummary.HistoryFetch.ServerHost.ShouldBe("octopus.example.com");
        dto.SourceSummary.FileName.ShouldBe("export.zip");
        _dataProvider.Verify(p => p.UpdateSessionAsync(session, dataVersion, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateHistoryFetch_AnotherChunkOwnsTheFetch_ThrowsConcurrencyWithoutWriting(bool storedHasChunk)
    {
        var session = Session(OctopusImportSessionState.Uploaded, new OctopusImportHistoryFetchDto { ChunkId = storedHasChunk ? Guid.NewGuid() : null });

        await Should.ThrowAsync<OctopusImportSessionConcurrencyException>(() => _service.UpdateHistoryFetchAsync(session.SessionId, 7, new OctopusImportHistoryFetchDto(), Guid.NewGuid()));

        _dataProvider.Verify(p => p.UpdateSessionAsync(It.IsAny<OctopusImportSession>(), It.IsAny<byte[]>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The key fingerprint is stored for the next chunk's binding check but never handed back in a session response.
    [Fact]
    public async Task UpdateHistoryFetch_StoresTheKeyBinding_ButNeverReturnsTheFingerprint()
    {
        var previous = Guid.NewGuid();
        var session = Session(OctopusImportSessionState.Uploaded, new OctopusImportHistoryFetchDto { ChunkId = previous });

        var dto = await _service.UpdateHistoryFetchAsync(session.SessionId, 7, new OctopusImportHistoryFetchDto { ChunkId = Guid.NewGuid(), UserId = "Users-7", KeyHmac = "0123abcd" }, previous);

        dto.SourceSummary.HistoryFetch.UserId.ShouldBe("Users-7");
        dto.SourceSummary.HistoryFetch.KeyHmac.ShouldBeNull();
        session.SourceSummaryJson.ShouldContain("\"keyHmac\":\"0123abcd\"");
    }

    [Fact]
    public async Task UpdateHistoryFetch_SessionAlreadyExtracted_Rejected()
    {
        var session = Session(OctopusImportSessionState.Extracted, null);

        await Should.ThrowAsync<OctopusImportSessionStateTransitionException>(() => _service.UpdateHistoryFetchAsync(session.SessionId, 7, new OctopusImportHistoryFetchDto(), null));

        _dataProvider.Verify(p => p.UpdateSessionAsync(It.IsAny<OctopusImportSession>(), It.IsAny<byte[]>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterTemporaryUpload_WithANewSummary_KeepsTheRecordedHistoryFetch()
    {
        var session = Session(OctopusImportSessionState.Uploaded, new OctopusImportHistoryFetchDto { ServerHost = "octopus.example.com", Done = true });

        var dto = await _service.RegisterTemporaryUploadAsync(session.SessionId, 7, new OctopusImportTemporaryUpload("/tmp/upload.zip", 10), new OctopusImportSourceSummaryDto { FileName = "second.zip" });

        dto.SourceSummary.FileName.ShouldBe("second.zip");
        dto.SourceSummary.HistoryFetch.ShouldNotBeNull().ServerHost.ShouldBe("octopus.example.com");
        dto.SourceSummary.HistoryFetch.Done.ShouldBeTrue();
    }

    private OctopusImportSession Session(OctopusImportSessionState state, OctopusImportHistoryFetchDto historyFetch)
    {
        var summary = new OctopusImportSourceSummaryDto { FileName = "export.zip", HistoryFetch = historyFetch };
        var session = new OctopusImportSession
        {
            SessionId = Guid.NewGuid(),
            OwnerUserId = 42,
            DestinationSpaceId = 7,
            State = state.ToString(),
            SourceSummaryJson = System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }),
            DataVersion = Guid.NewGuid().ToByteArray(),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            LastStateChangedAt = DateTimeOffset.UtcNow
        };

        _dataProvider.Setup(p => p.GetSessionAsync(session.SessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        return session;
    }
}

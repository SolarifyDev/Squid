using System.IO;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportTemporaryUploadCleanupServiceTests
{
    private readonly Mock<IOctopusImportSessionDataProvider> _dataProvider = new();
    private readonly Mock<IOctopusImportTemporaryUploadStore> _uploadStore = new();
    private readonly TestTemporaryUploadSettings _settings = new();
    private readonly OctopusImportTemporaryUploadCleanupService _service;

    public OctopusImportTemporaryUploadCleanupServiceTests()
    {
        _settings.CleanupBatchSize = 25;
        _settings.FailedRetentionPeriod = TimeSpan.FromHours(4);
        _settings.InterruptedImportGracePeriod = TimeSpan.FromHours(2);

        _dataProvider
            .Setup(p => p.ExpireSessionsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _dataProvider
            .Setup(p => p.MarkInterruptedImportsFailedAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _dataProvider
            .Setup(p => p.GetTemporaryUploadCleanupCandidatesAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _dataProvider
            .Setup(p => p.MarkTemporaryUploadCleanedAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _service = new OctopusImportTemporaryUploadCleanupService(
            _dataProvider.Object,
            _uploadStore.Object,
            _settings);
    }

    [Fact]
    public async Task EnforceCleanupAsync_ExpiresSessionsAndMarksInterruptedImportsBeforeCleaningFiles()
    {
        _dataProvider
            .Setup(p => p.ExpireSessionsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        _dataProvider
            .Setup(p => p.MarkInterruptedImportsFailedAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                TimeSpan.FromHours(4),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var outcome = await _service.EnforceCleanupAsync(CancellationToken.None);

        outcome.ExpiredSessions.ShouldBe(2);
        outcome.InterruptedImportsFailed.ShouldBe(1);
        _dataProvider.Verify(p => p.GetTemporaryUploadCleanupCandidatesAsync(
            It.IsAny<DateTimeOffset>(),
            25,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnforceCleanupAsync_CleansEligibleTerminalUploadAndMarksSessionCleaned()
    {
        var sessionId = Guid.NewGuid();
        const string path = "/tmp/squid-octopus-import-uploads/upload.zip";
        _dataProvider
            .Setup(p => p.GetTemporaryUploadCleanupCandidatesAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new OctopusImportTemporaryUploadCleanupCandidate(
                    sessionId,
                    path,
                    OctopusImportSessionState.Succeeded,
                    DateTimeOffset.UtcNow.AddMinutes(-1))
            ]);

        var outcome = await _service.EnforceCleanupAsync(CancellationToken.None);

        outcome.Scanned.ShouldBe(1);
        outcome.Cleaned.ShouldBe(1);
        outcome.Failed.ShouldBe(0);
        _uploadStore.Verify(s => s.SecureDeleteAsync(sessionId, path, It.IsAny<CancellationToken>()), Times.Once);
        _dataProvider.Verify(p => p.MarkTemporaryUploadCleanedAsync(
            sessionId,
            path,
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnforceCleanupAsync_WhenDeleteFails_RecordsFailureAndLeavesCandidateForRetry()
    {
        var sessionId = Guid.NewGuid();
        const string path = "/tmp/squid-octopus-import-uploads/upload.zip";
        _dataProvider
            .Setup(p => p.GetTemporaryUploadCleanupCandidatesAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new OctopusImportTemporaryUploadCleanupCandidate(
                    sessionId,
                    path,
                    OctopusImportSessionState.Expired,
                    DateTimeOffset.UtcNow.AddMinutes(-1))
            ]);
        _uploadStore
            .Setup(s => s.SecureDeleteAsync(sessionId, path, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk denied"));

        var outcome = await _service.EnforceCleanupAsync(CancellationToken.None);

        outcome.Cleaned.ShouldBe(0);
        outcome.Failed.ShouldBe(1);
        _dataProvider.Verify(p => p.MarkTemporaryUploadCleanedAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _dataProvider.Verify(p => p.MarkTemporaryUploadCleanupFailedAsync(
            sessionId,
            path,
            "disk denied",
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class TestTemporaryUploadSettings : IOctopusImportTemporaryUploadSettings
    {
        public string RootPath { get; set; } = Path.Combine(Path.GetTempPath(), "squid-octopus-import-upload-tests");

        public TimeSpan DefaultRetentionPeriod { get; set; } = TimeSpan.FromHours(24);

        public TimeSpan FailedRetentionPeriod { get; set; } = TimeSpan.FromHours(24);

        public TimeSpan InterruptedImportGracePeriod { get; set; } = TimeSpan.FromHours(6);

        public int CleanupBatchSize { get; set; } = 100;

        public int SecureDeleteBufferBytes { get; set; } = 81920;
    }
}

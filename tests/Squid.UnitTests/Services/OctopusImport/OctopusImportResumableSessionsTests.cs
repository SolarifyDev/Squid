using System.Reflection;
using System.Text.Json;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Attributes;
using Squid.Message.Contracts;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Requests.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>Reopening an import session after a page reload: where each session continues, the list's display contract and its secret hygiene.</summary>
public class OctopusImportResumableSessionsTests
{
    private const string ServerFormat = OctopusImportServerSourceService.ServerSourceFormat;

    private readonly Mock<IOctopusImportSessionDataProvider> _dataProvider = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IOctopusImportTemporaryUploadSettings> _settings = new();
    private readonly OctopusImportSessionService _service;

    public OctopusImportResumableSessionsTests()
    {
        _settings.SetupGet(s => s.DefaultRetentionPeriod).Returns(TimeSpan.FromHours(24));
        _settings.SetupGet(s => s.MaxSessionAge).Returns(TimeSpan.FromHours(168));
        _currentUser.SetupGet(u => u.Id).Returns(42);
        _service = new OctopusImportSessionService(_dataProvider.Object, _currentUser.Object, _settings.Object);
    }

    // ── Resume step ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OctopusImportSessionState.Uploaded, true, false, true, false, OctopusImportResumeStep.ResumeRead)]
    [InlineData(OctopusImportSessionState.Uploaded, true, true, true, false, OctopusImportResumeStep.ContinueAfterRead)]
    [InlineData(OctopusImportSessionState.Uploaded, false, false, true, false, OctopusImportResumeStep.ContinueAfterRead)]
    [InlineData(OctopusImportSessionState.Uploaded, false, false, false, false, null)]
    [InlineData(OctopusImportSessionState.Extracted, false, false, true, false, OctopusImportResumeStep.ContinueAfterRead)]
    [InlineData(OctopusImportSessionState.Extracted, true, true, true, false, OctopusImportResumeStep.ContinueAfterRead)]
    [InlineData(OctopusImportSessionState.Previewed, true, true, true, false, OctopusImportResumeStep.Preview)]
    [InlineData(OctopusImportSessionState.Validated, false, false, true, false, OctopusImportResumeStep.Preview)]
    [InlineData(OctopusImportSessionState.Validated, true, true, true, true, OctopusImportResumeStep.RetryImport)]
    [InlineData(OctopusImportSessionState.Importing, true, true, true, false, OctopusImportResumeStep.FollowImport)]
    [InlineData(OctopusImportSessionState.Succeeded, true, true, true, false, null)]
    [InlineData(OctopusImportSessionState.Failed, false, false, true, true, null)]
    [InlineData(OctopusImportSessionState.Expired, true, false, true, false, null)]
    public void Of_StateSourceAndRead_GivesStep(OctopusImportSessionState state, bool server, bool readDone, bool hasUpload, bool hasConfirmationFailure, OctopusImportResumeStep? expected)
    {
        var summary = Summary(server, readDone);

        OctopusImportResumeSteps.Of(state, summary, hasUpload, hasConfirmationFailure).ShouldBe(expected);
    }

    [Fact]
    public void Of_ServerSessionWithoutCursor_ResumesTheRead()
        => OctopusImportResumeSteps.Of(OctopusImportSessionState.Uploaded, new OctopusImportSourceSummaryDto { DetectedFormat = ServerFormat }, true, false).ShouldBe(OctopusImportResumeStep.ResumeRead);

    // ── Server URL ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://octopus.acme.com", "https://octopus.acme.com")]
    [InlineData("https://octopus.acme.com/", "https://octopus.acme.com")]
    [InlineData("  HTTPS://Octopus.Acme.com:8443/octopus/  ", "https://octopus.acme.com:8443/octopus")]
    [InlineData("https://user:secret@octopus.acme.com/octopus", "https://octopus.acme.com/octopus")]
    [InlineData("https://octopus.acme.com/?apikey=API-SECRET", "https://octopus.acme.com")]
    [InlineData("https://octopus.acme.com/octopus#API-SECRET", "https://octopus.acme.com/octopus")]
    [InlineData("http://10.0.0.5:8080", "http://10.0.0.5:8080")]
    [InlineData("ftp://octopus.acme.com", null)]
    [InlineData("octopus.acme.com", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Sanitize_KeepsSchemeHostAndPathOnly(string input, string expected) => OctopusImportServerUrl.Sanitize(input).ShouldBe(expected);

    // ── Validated plan choices ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SavedChoices_NoPlan_Empty(string json) => OctopusImportValidatedPlans.SavedChoices(json).SelectedOr("Projects-1", true).ShouldBeTrue();

    [Fact]
    public void SavedChoices_PlanWithDeselectedRoot_ReadsTheChoice()
    {
        var plan = new OctopusImportValidatedPlanDto { PreviewPlan = new OctopusImportPreviewPlanDto { Resources = [new OctopusImportResourceResultDto { SourceId = "Projects-1", Choice = new OctopusImportChoiceDto { Selected = false } }] } };
        var json = JsonSerializer.Serialize(plan, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var choices = OctopusImportValidatedPlans.SavedChoices(json);

        choices.SelectedOr("Projects-1", true).ShouldBeFalse();
        choices.SelectedOr("Projects-2", true).ShouldBeTrue();
    }

    // ── Request ────────────────────────────────────────────────────────────

    [Fact]
    public void ListRequest_RequiresProjectCreate_AndIsSpaceScoped()
    {
        typeof(ListOctopusImportSessionsRequest).GetCustomAttribute<RequiresPermissionAttribute>().ShouldNotBeNull().Permission.ShouldBe(Permission.ProjectCreate);
        typeof(ISpaceScoped).IsAssignableFrom(typeof(ListOctopusImportSessionsRequest)).ShouldBeTrue();
    }

    // ── Listing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_AsksForTheCurrentUsersSessionsInTheSpace_CappedAtTwenty()
    {
        Rows();

        await _service.ListResumableSessionsAsync(7);

        _dataProvider.Verify(p => p.GetResumableSessionsNoTrackingAsync(42, 7, It.IsAny<DateTimeOffset>(), OctopusImportSessionService.MaxListedSessions, It.IsAny<CancellationToken>()), Times.Once);
        OctopusImportSessionService.MaxListedSessions.ShouldBe(20);
    }

    [Fact]
    public async Task List_NoUser_Unauthorized()
    {
        _currentUser.SetupGet(u => u.Id).Returns((int?)null);

        await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.ListResumableSessionsAsync(7));
    }

    [Fact]
    public async Task List_SessionsWithoutAStep_Dropped()
    {
        Rows(Row(OctopusImportSessionState.Uploaded, Summary(false, false), hasUpload: false), Row(OctopusImportSessionState.Previewed, Summary(false, false)));

        var listed = await _service.ListResumableSessionsAsync(7);

        listed.ShouldHaveSingleItem().ResumeStep.ShouldBe(OctopusImportResumeStep.Preview);
    }

    [Fact]
    public async Task Map_ServerRead_DisplayContract()
    {
        var summary = Summary(true, false);
        summary.SelectedProjectIds = ["Projects-1", "Projects-2", "Projects-3"];
        summary.HistoryFetch.ChosenProjectNames = ["Api", "Worker"];
        summary.HistoryFetch.ProjectIds = ["Projects-1", "Projects-2", "Projects-3"];
        summary.HistoryFetch.ProjectIndex = 1;
        summary.HistoryFetch.Phase = OctopusHistoryFetchPhase.Releases;
        var row = Row(OctopusImportSessionState.Uploaded, summary);
        Rows(row);

        var dto = (await _service.ListResumableSessionsAsync(7)).ShouldHaveSingleItem();

        dto.SessionId.ShouldBe(row.SessionId);
        dto.SourceKind.ShouldBe(OctopusImportSourceKind.OctopusServer);
        dto.ResumeStep.ShouldBe(OctopusImportResumeStep.ResumeRead);
        dto.ServerUrl.ShouldBe("https://octopus.acme.com/octopus");
        dto.ServerHost.ShouldBe("octopus.acme.com");
        dto.OctopusSpaceId.ShouldBe("Spaces-1");
        dto.FileName.ShouldBeNull();
        dto.SelectedProjectCount.ShouldBe(3);
        dto.SelectedProjectNames.ShouldBe(["Api", "Worker"]);
        dto.ReadProgress.ShouldNotBeNull().ProjectIndex.ShouldBe(1);
        dto.ReadProgress.ProjectCount.ShouldBe(3);
        dto.ReadProgress.Phase.ShouldBe(OctopusHistoryFetchPhase.Releases);
        dto.CreatedAt.ShouldBe(row.CreatedDate);
        dto.UpdatedAt.ShouldBe(row.LastModifiedDate);
        dto.ExpiresAt.ShouldBe(row.ExpiresAt);
        dto.MaxExpiresAt.ShouldBe(row.CreatedDate.AddHours(168));
    }

    [Fact]
    public async Task Map_ExportFile_FileNameNoServerNoProgress()
    {
        Rows(Row(OctopusImportSessionState.Extracted, Summary(false, false)));

        var dto = (await _service.ListResumableSessionsAsync(7)).ShouldHaveSingleItem();

        dto.SourceKind.ShouldBe(OctopusImportSourceKind.ExportFile);
        dto.FileName.ShouldBe("export.zip");
        dto.ServerUrl.ShouldBeNull();
        dto.ReadProgress.ShouldBeNull();
        dto.ReadActive.ShouldBeFalse();
    }

    [Fact]
    public async Task Map_WholeSpace_CountNull()
    {
        Rows(Row(OctopusImportSessionState.Uploaded, Summary(true, false)));

        (await _service.ListResumableSessionsAsync(7)).ShouldHaveSingleItem().SelectedProjectCount.ShouldBeNull();
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(-10, false)]
    public async Task Map_LeaseHeld_ReadActive(int leaseMinutesFromNow, bool expected)
    {
        var summary = Summary(true, false);
        summary.HistoryFetch.ChunkId = Guid.NewGuid();
        summary.HistoryFetch.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(leaseMinutesFromNow);
        Rows(Row(OctopusImportSessionState.Uploaded, summary));

        (await _service.ListResumableSessionsAsync(7)).ShouldHaveSingleItem().ReadActive.ShouldBe(expected);
    }

    // The list is built field by field: the key's fingerprint, the Octopus user, staging and cursor internals never reach a response.
    [Fact]
    public async Task Map_NeverExposesKeyMaterialOrPaths()
    {
        var summary = Summary(true, false);
        summary.HistoryFetch.KeyHmac = "SENTINEL-HMAC";
        summary.HistoryFetch.UserId = "SENTINEL-USER";
        summary.HistoryFetch.StagingId = "SENTINEL-STAGING";
        summary.HistoryFetch.InstallationId = "SENTINEL-INSTALLATION";
        summary.HistoryFetch.ClosureReferences = ["SENTINEL-CLOSURE"];
        summary.Files = [new OctopusImportSourceFileSummaryDto { Path = "/var/squid/SENTINEL-PATH" }];
        Rows(Row(OctopusImportSessionState.Uploaded, summary));

        var json = JsonSerializer.Serialize(await _service.ListResumableSessionsAsync(7));

        json.ShouldNotContain("SENTINEL");
        json.ShouldNotContain("API-");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void Rows(params OctopusImportResumableSessionRow[] rows)
        => _dataProvider.Setup(p => p.GetResumableSessionsNoTrackingAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(rows);

    private static OctopusImportResumableSessionRow Row(OctopusImportSessionState state, OctopusImportSourceSummaryDto summary, bool hasUpload = true)
    {
        var created = DateTimeOffset.UtcNow.AddHours(-2);

        return new OctopusImportResumableSessionRow(Guid.NewGuid(), state.ToString(), JsonSerializer.Serialize(summary, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), hasUpload, false, created, created.AddHours(1), created.AddMinutes(30), created.AddHours(20));
    }

    private static OctopusImportSourceSummaryDto Summary(bool server, bool readDone)
        => server
            ? new OctopusImportSourceSummaryDto { FileName = "history.zip", DetectedFormat = ServerFormat, HistoryFetch = new OctopusImportHistoryFetchDto { ServerUrl = "https://octopus.acme.com/octopus", ServerHost = "octopus.acme.com", OctopusSpaceId = "Spaces-1", Done = readDone } }
            : new OctopusImportSourceSummaryDto { FileName = "export.zip", DetectedFormat = "OctopusExport" };
}

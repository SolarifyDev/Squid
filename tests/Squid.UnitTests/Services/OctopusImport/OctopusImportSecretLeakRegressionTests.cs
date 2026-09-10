using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Moq;
using Serilog;
using Serilog.Events;
using Shouldly;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Squid.UnitTests.Support;
using Xunit;

namespace Squid.UnitTests.Services.OctopusImport;

[Collection(GlobalStateSerialisedCollection.Name)]
public class OctopusImportSecretLeakRegressionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Mock<IOctopusImportSessionDataProvider> _dataProvider = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IOctopusImportTemporaryUploadSettings> _temporaryUploadSettings = new();
    private readonly Mock<IOctopusImportTemporaryUploadStore> _uploadStore = new();
    private readonly OctopusImportSessionService _sessionService;
    private readonly OctopusImportPreviewPlanner _previewPlanner = new();
    private readonly OctopusImportPreviewValidator _previewValidator = new();
    private readonly OctopusImportTemporaryUploadCleanupService _cleanupService;

    public OctopusImportSecretLeakRegressionTests()
    {
        _currentUser.SetupGet(u => u.Id).Returns(42);
        _temporaryUploadSettings.SetupGet(s => s.FailedRetentionPeriod).Returns(TimeSpan.FromHours(4));
        _temporaryUploadSettings.SetupGet(s => s.DefaultRetentionPeriod).Returns(TimeSpan.FromHours(24));
        _temporaryUploadSettings.SetupGet(s => s.CleanupBatchSize).Returns(25);
        _temporaryUploadSettings.SetupGet(s => s.InterruptedImportGracePeriod).Returns(TimeSpan.FromHours(2));

        _sessionService = new OctopusImportSessionService(_dataProvider.Object, _currentUser.Object, _temporaryUploadSettings.Object);
        _cleanupService = new OctopusImportTemporaryUploadCleanupService(_dataProvider.Object, _uploadStore.Object, _temporaryUploadSettings.Object);
    }

    [Fact]
    public async Task FinalSessionResponses_RedactSensitiveSourceSummaryPayloadsAndResultData()
    {
        OctopusImportSession inserted = null;

        _dataProvider
            .Setup(p => p.AddSessionAsync(It.IsAny<OctopusImportSession>(), true, It.IsAny<CancellationToken>()))
            .Callback<OctopusImportSession, bool, CancellationToken>((session, _, _) => inserted = session)
            .Returns(Task.CompletedTask);

        var sourceSummary = new OctopusImportSourceSummaryDto
        {
            FileName = "Next Chat Export.zip",
            ContentType = "application/zip; password=archive-secret",
            DetectedFormat = "Zip",
            Sha256 = "Authorization=Bearer archive-secret",
            Files =
            [
                new OctopusImportSourceFileSummaryDto
                {
                    Path = "manifest.json",
                    DocumentType = "Manifest",
                    SizeBytes = 123,
                    Sha256 = "password=file-summary-secret"
                }
            ]
        };

        var created = await _sessionService.CreateSessionAsync(7, sourceSummary, DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        inserted.ShouldNotBeNull();
        inserted.SourceSummaryJson.ShouldNotContain("archive-secret");
        inserted.SourceSummaryJson.ShouldNotContain("file-summary-secret");
        created.SourceSummary.ContentType.ShouldBe(OctopusImportRedaction.RedactedValue);
        created.SourceSummary.Sha256.ShouldBe(OctopusImportRedaction.RedactedValue);
        created.SourceSummary.Files.Single().Sha256.ShouldBe(OctopusImportRedaction.RedactedValue);
        Serialized(created).ShouldNotContain("archive-secret");
        Serialized(created).ShouldNotContain("file-summary-secret");

        var sessionId = Guid.NewGuid();
        var importingSession = new OctopusImportSession
        {
            SessionId = sessionId,
            OwnerUserId = 42,
            DestinationSpaceId = 7,
            State = OctopusImportSessionState.Importing.ToString(),
            SourceSummaryJson = "{}",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            LastStateChangedAt = DateTimeOffset.UtcNow
        };

        _dataProvider
            .Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(importingSession);

        var completed = await _sessionService.RecordResultAsync(
            sessionId,
            7,
            OctopusImportSessionState.Succeeded,
            new OctopusImportSessionResultDto
            {
                Resources =
                [
                    new OctopusImportResourceResultDto
                    {
                        SourceId = "Projects-1",
                        SourceType = "Project",
                        SourceName = "rotate password secret",
                        PreviewAction = OctopusImportPreviewAction.Create,
                        OutcomeState = OctopusImportResourceOutcomeState.Created,
                        DestinationId = 100,
                        Diagnostics =
                        [
                            new OctopusImportDiagnosticDto
                            {
                                Severity = OctopusImportCompatibilitySeverity.Warning,
                                Code = "octopus.test.result",
                                Message = "resource token=resource-secret-value",
                                ResourceType = "DeploymentAction",
                                SourceId = "Actions-1",
                                ResourceName = "rotate password secret"
                            }
                        ]
                    }
                ],
                IdMappings =
                [
                    new OctopusImportIdMappingDto
                    {
                        SourceId = "Variables-1",
                        SourceType = "Variable",
                        SourceName = "api-key secret",
                        DestinationType = "Variable",
                        DestinationId = 200,
                        OutcomeState = OctopusImportResourceOutcomeState.Created
                    }
                ],
                Diagnostics =
                [
                    new OctopusImportDiagnosticDto
                    {
                        Severity = OctopusImportCompatibilitySeverity.Warning,
                        Code = "octopus.test.result.diagnostic",
                        Message = "result diagnostic token=result-diagnostic-secret",
                        ResourceType = "DeploymentProcess",
                        SourceId = "deploymentprocess-Projects-1",
                        ResourceName = "password secret"
                    }
                ]
            },
            CancellationToken.None);

        importingSession.ResultJson.ShouldNotContain("resource-secret-value");
        importingSession.ResultJson.ShouldNotContain("result-diagnostic-secret");
        importingSession.ResultJson.ShouldNotContain("api-key secret");
        completed.Result.Resources.Single().SourceName.ShouldBe(OctopusImportRedaction.RedactedValue);
        completed.Result.Diagnostics.Single().Message.ShouldContain(OctopusImportRedaction.RedactedValue);
        Serialized(completed.Result).ShouldNotContain("resource-secret-value");
        Serialized(completed.Result).ShouldNotContain("result-diagnostic-secret");
        Serialized(completed.Result).ShouldNotContain("api-key secret");
    }

    [Fact]
    public void PreviewAndValidationResponses_RedactSensitiveSourceValuesFromAllReportedPaths()
    {
        var variable = Resource(
            "Variables-Secret",
            "ApiKey",
            new OctopusVariableDto
            {
                Id = "Variables-Secret",
                Name = "ApiKey",
                Type = "Sensitive",
                IsSensitive = true,
                Value = "variable-secret-value",
                Scope = { ["Environment"] = ["Environments-1"] }
            },
            OctopusResourceKind.Variable,
            OctopusDocumentKind.VariableSet);
        var feed = Resource(
            "Feeds-1",
            "Docker",
            new OctopusFeedDto
            {
                Id = "Feeds-1",
                Name = "Docker",
                FeedType = "Docker",
                FeedUri = "https://registry.example",
                Username = "feed-user",
                Password = "feed-password-secret"
            },
            OctopusResourceKind.Feed,
            OctopusDocumentKind.Feed);
        var account = Resource(
            "Accounts-1",
            "AWS",
            new OctopusAccountDto
            {
                Id = "Accounts-1",
                Name = "AWS",
                Credentials = Json("""{"AccessKey":"AKIA-SOURCE","SecretKey":"account-secret-key"}""")
            },
            OctopusResourceKind.Account,
            OctopusDocumentKind.Account);
        var certificate = Resource(
            "Certificates-1",
            "TLS",
            new OctopusCertificateDto
            {
                Id = "Certificates-1",
                Name = "TLS",
                HasPrivateKey = true,
                CertificateData = Json("""{"Pfx":"certificate-pfx-secret"}""")
            },
            OctopusResourceKind.Certificate,
            OctopusDocumentKind.Certificate);
        var machine = Resource(
            "Machines-1",
            "EKS target",
            new OctopusMachineDto
            {
                Id = "Machines-1",
                Name = "EKS target",
                Endpoint = Json("""{"ProviderConfig":{"Token":"endpoint-token-secret"}}""")
            },
            OctopusResourceKind.Machine,
            OctopusDocumentKind.Machine);

        var plan = new OctopusImportDependencyPlan([variable, feed, account, certificate, machine], [], [], [], []);

        var preview = _previewPlanner.BuildPreviewPlan(plan, new OctopusImportConflictDiscoveryResult([]));

        preview.Resources.Single(r => r.SourceId == "Variables-Secret").RequiredInputs.Single().HasSourceValue.ShouldBeTrue();
        preview.Resources.SelectMany(r => r.Diagnostics).ShouldNotBeEmpty();
        Serialized(preview).ShouldNotContain("variable-secret-value");
        Serialized(preview).ShouldNotContain("feed-user");
        Serialized(preview).ShouldNotContain("feed-password-secret");
        Serialized(preview).ShouldNotContain("AKIA-SOURCE");
        Serialized(preview).ShouldNotContain("account-secret-key");
        Serialized(preview).ShouldNotContain("certificate-pfx-secret");
        Serialized(preview).ShouldNotContain("endpoint-token-secret");

        var graph = new OctopusResourceGraph(
            [variable],
            [
                new OctopusResourceReference(
                    variable.SourceId,
                    variable.Kind,
                    OctopusResourceReferenceKind.Environment,
                    "Environments-Missing",
                    OctopusResourceKind.Environment,
                    null,
                    true,
                    false)
            ],
            [],
            []);

        var validation = _previewValidator.Validate(graph, plan, new OctopusImportConflictDiscoveryResult([]), preview);

        validation.HasBlockers.ShouldBeTrue();
        validation.RequiredInputs.Single().InputKey.ShouldBe(preview.RequiredInputs.Single().InputKey);
        Serialized(validation).ShouldNotContain("variable-secret-value");
        Serialized(validation).ShouldNotContain("feed-user");
        Serialized(validation).ShouldNotContain("feed-password-secret");
        Serialized(validation).ShouldNotContain("AKIA-SOURCE");
        Serialized(validation).ShouldNotContain("account-secret-key");
        Serialized(validation).ShouldNotContain("certificate-pfx-secret");
        Serialized(validation).ShouldNotContain("endpoint-token-secret");
    }

    [Fact]
    public async Task CleanupWarningLog_RedactsExceptionTextAndPersistedCleanupError()
    {
        var (sink, restore) = InstallCapturingLogger();

        try
        {
            var sessionId = Guid.NewGuid();
            const string path = "/tmp/squid-octopus-import-uploads/upload.zip";

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
                .ReturnsAsync([
                    new OctopusImportTemporaryUploadCleanupCandidate(
                        sessionId,
                        path,
                        OctopusImportSessionState.Expired,
                        DateTimeOffset.UtcNow.AddMinutes(-1))
                ]);
            _uploadStore
                .Setup(s => s.SecureDeleteAsync(sessionId, path, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("cleanup failed for password=cleanup-secret-token and Authorization=Bearer cleanup-secret-token"));

            var outcome = await _cleanupService.EnforceCleanupAsync(CancellationToken.None);

            outcome.Failed.ShouldBe(1);

            var warning = sink.Events.Single(e => e.Level == LogEventLevel.Warning);
            warning.RenderMessage().ShouldNotContain("cleanup-secret-token");
            warning.RenderMessage().ShouldContain(OctopusImportRedaction.RedactedValue);
            warning.Exception.ShouldBeNull();

            _dataProvider.Verify(p => p.MarkTemporaryUploadCleanupFailedAsync(
                sessionId,
                path,
                OctopusImportRedaction.RedactedValue,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            restore();
        }
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static OctopusResourceNode Resource(
        string sourceId,
        string name,
        object source,
        OctopusResourceKind kind,
        OctopusDocumentKind documentKind)
        => new(
            sourceId,
            name,
            kind,
            documentKind,
            $"{sourceId}.json",
            null,
            null,
            false,
            source);

    private static (CapturingLogSink Sink, Action Restore) InstallCapturingLogger()
    {
        var original = Log.Logger;
        var sink = new CapturingLogSink();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        return (sink, () => Log.Logger = original);
    }

    private static string Serialized<T>(T value)
        => JsonSerializer.Serialize(value, JsonOptions);

    private sealed class CapturingLogSink : Serilog.Core.ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}

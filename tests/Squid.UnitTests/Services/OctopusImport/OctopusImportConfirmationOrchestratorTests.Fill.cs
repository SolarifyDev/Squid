using System.Linq;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.ExternalFeed;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;
using Squid.UnitTests.Services.OctopusImport.Secrets;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// Filling a secret an earlier import left <c>Missing</c>, at confirmation: re-decided from the server's own data (never the posted
/// plan), written only through the owning service's compare-and-set, and every outcome settles the row — <c>Filled</c> when written
/// (or empty in Octopus too), <c>Superseded</c> when Squid holds a value or the variable is gone; anything else leaves it <c>Missing</c>.
/// </summary>
public partial class OctopusImportConfirmationOrchestratorTests
{
    private const string FillPassword = "fill-export-password";
    private const string FillPlaintext = "db-secret-from-file";

    [Theory]
    [InlineData(SecretSlotState.Filled, OctopusImportResourceOutcomeState.SecretFilled, OctopusImportSourceMapQueries.FilledOutcome, OctopusImportPreviewDiagnosticCodes.SecretFillFromExport)]
    [InlineData(SecretSlotState.AlreadySet, OctopusImportResourceOutcomeState.Reused, OctopusImportSourceMapQueries.SupersededOutcome, OctopusImportPreviewDiagnosticCodes.SecretSetInSquid)]
    [InlineData(SecretSlotState.NotFound, OctopusImportResourceOutcomeState.Reused, OctopusImportSourceMapQueries.SupersededOutcome, OctopusImportPreviewDiagnosticCodes.SecretFillTargetGone)]
    [InlineData(SecretSlotState.Empty, OctopusImportResourceOutcomeState.Reused, null, OctopusImportPreviewDiagnosticCodes.SecretStillMissing)]
    public async Task PostedFill_WrittenByTheServiceCompareAndSet_RowSettledByWhatItReturns(SecretSlotState serviceResult, OctopusImportResourceOutcomeState expectedOutcome, string expectedRow, string expectedCode)
    {
        var fill = FillScenario(FillPlaintext);
        fill.Variables.Setup(v => v.FillEmptySensitiveValueAsync(1002, It.IsAny<VariableIdentity>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(serviceResult);

        var result = await fill.Harness.Sut.ConfirmAsync(fill.Harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, string.Join("; ", fill.Harness.SessionService.RecordedResult.Resources.SelectMany(r => r.Diagnostics).Select(d => d.Message)));
        fill.Variables.Verify(v => v.FillEmptySensitiveValueAsync(1002, It.Is<VariableIdentity>(i => i.Name == "ApiKey" && i.Type == VariableType.Password && i.Scopes.Count == 0), FillPlaintext, It.IsAny<CancellationToken>()), Times.Once);

        var outcome = Outcome(fill.Harness, fill.Nodes.VariableOne);
        outcome.OutcomeState.ShouldBe(expectedOutcome);
        outcome.Diagnostics.ShouldContain(d => d.Code == expectedCode);
        outcome.Diagnostics.ShouldAllBe(d => !d.Message.Contains(FillPlaintext));

        var rows = SecretRows(fill.Upserted);
        if (expectedRow == null) rows.ShouldBeEmpty("nothing written and nothing settled: the value stays missing");
        else rows.ShouldHaveSingleItem().ShouldSatisfyAllConditions(r => r.OutcomeState.ShouldBe(expectedRow), r => r.SourceId.ShouldBe(fill.RowKey), r => r.DestinationId.ShouldBe(1002));
    }

    // The result line names the project the preview named — but only when the posted name points at the very variable set written.
    [Theory]
    [InlineData(1002, "into Squid project \"Web\" (project 1001, variable set 1002)")]
    [InlineData(4242, "into Squid variable set 1002")]
    public async Task Filled_ResultNamesThePreviewedDestinationOnlyWhenItIsTheRowWritten(int postedVariableSetId, string expected)
    {
        var fill = FillScenario(FillPlaintext);
        fill.Variables.Setup(v => v.FillEmptySensitiveValueAsync(1002, It.IsAny<VariableIdentity>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(SecretSlotState.Filled);
        fill.Harness.Request.PreviewPlan.Resources.Single(r => r.SourceId == fill.Nodes.VariableOne.SourceId).FillDestination = new OctopusImportFillDestinationDto { OwnerKind = OctopusImportFillDestinationDto.ProjectOwner, OwnerId = 1001, OwnerName = "Web", VariableSetId = postedVariableSetId };

        await fill.Harness.Sut.ConfirmAsync(fill.Harness.Request, CancellationToken.None);

        var outcome = Outcome(fill.Harness, fill.Nodes.VariableOne);

        outcome.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillFromExport).Message.ShouldContain(expected);
        outcome.FillDestination?.VariableSetId.ShouldBe(postedVariableSetId == 1002 ? 1002 : null, "a posted destination that is not the row written never reaches the result");
    }

    // An unproven file is refused before any resource runs: under the space lock its origin no longer links to the space's rows.
    [Theory]
    [InlineData("unproven", OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven)]
    [InlineData("no-missing-row", OctopusImportPreviewDiagnosticCodes.StalePreviewPlan)]
    [InlineData("settled-row", OctopusImportPreviewDiagnosticCodes.StalePreviewPlan)]
    [InlineData("no-export-password", OctopusImportPreviewDiagnosticCodes.StalePreviewPlan)]
    public async Task PostedFill_NotBackedByTheServersOwnData_FailsStaleNothingWritten(string reason, string expectedCode)
    {
        var fill = FillScenario(FillPlaintext, graphInstallationId: reason == "unproven" ? null : InstallationId, rowOutcome: reason switch { "no-missing-row" => null, "settled-row" => OctopusImportSourceMapQueries.SupersededOutcome, _ => OctopusImportSourceMapQueries.MissingOutcome }, password: reason == "no-export-password" ? null : FillPassword);

        var result = await fill.Harness.Sut.ConfirmAsync(fill.Harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Failed);
        fill.Harness.SessionService.RecordedResult.Diagnostics.Concat(Outcome(fill.Harness, fill.Nodes.VariableOne).Diagnostics).ShouldContain(d => d.Code == expectedCode);
        fill.Variables.Verify(v => v.FillEmptySensitiveValueAsync(It.IsAny<int>(), It.IsAny<VariableIdentity>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The client may downgrade a fill to "already imported": nothing is filled, but Squid's state still settles the row honestly.
    [Theory]
    [InlineData(true, FillPlaintext, OctopusImportSourceMapQueries.SupersededOutcome)]   // a user set it: kept, Superseded
    [InlineData(false, FillPlaintext, null)]                                            // still empty: stays Missing (not filled without the plan)
    [InlineData(false, null, OctopusImportSourceMapQueries.FilledOutcome)]               // empty in Octopus too (proven file): Filled, no write
    public async Task AlreadyImportedWithAMissingRow_SettledFromSquidsStateWithoutWriting(bool squidHasValue, string fileValue, string expectedRow)
    {
        var fill = FillScenario(fileValue, action: OctopusImportPreviewAction.AlreadyImported);
        fill.Variables.Setup(v => v.GetSensitiveVariableSlotsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync([new SensitiveVariableSlot(9001, 1002, VariableIdentity.Of("ApiKey", VariableType.Password, []), squidHasValue)]);

        var result = await fill.Harness.Sut.ConfirmAsync(fill.Harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        fill.Variables.Verify(v => v.FillEmptySensitiveValueAsync(It.IsAny<int>(), It.IsAny<VariableIdentity>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        var rows = SecretRows(fill.Upserted);
        if (expectedRow == null) rows.ShouldBeEmpty();
        else rows.ShouldHaveSingleItem().OutcomeState.ShouldBe(expectedRow);
    }

    [Fact]
    public async Task FeedFill_UsernameAndDecryptedPasswordGoToTheFeedServiceCompareAndSet()
    {
        var nodes = BuildRichNodes();
        var feed = Node("Feeds-1", OctopusResourceKind.Feed, OctopusDocumentKind.Feed, "Registry", new OctopusFeedDto { Id = "Feeds-1", Name = "Registry", FeedType = "Docker", FeedUri = "https://registry.example.com", Username = "robot", Password = OctopusExportTestCipher.Encrypt("registry-token", FillPassword) }, null);
        var prior = PriorWith([(OctopusResourceKind.Feed.ToString(), "Feeds-1", 801, "Created"), (OctopusImportSourceMapQueries.FeedSecretSourceType, "Feeds-1", 801, OctopusImportSourceMapQueries.MissingOutcome)]);
        var upserted = new List<OctopusImportSourceMapRow>();
        var feeds = new Mock<IExternalFeedService>();
        feeds.Setup(f => f.FillMissingCredentialsAsync(801, "robot", "registry-token", It.IsAny<CancellationToken>())).ReturnsAsync(SecretSlotState.Filled);
        var harness = CreateHarness(nodes, BuildDependencyPlan([feed]), Plan((feed, OctopusImportPreviewAction.FillMissingSecret, 801)), true, false, false, sourceMap: SourceMap(prior, upserted: upserted).Object, secretsAccessor: FileSession(FillPassword), externalFeedService: feeds.Object, graphInstallationId: InstallationId);

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        feeds.Verify(f => f.FillMissingCredentialsAsync(801, "robot", "registry-token", It.IsAny<CancellationToken>()), Times.Once);
        Outcome(harness, feed).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.SecretFilled);
        SecretRows(upserted).ShouldHaveSingleItem().ShouldSatisfyAllConditions(r => r.SourceType.ShouldBe(OctopusImportSourceMapQueries.FeedSecretSourceType), r => r.OutcomeState.ShouldBe(OctopusImportSourceMapQueries.FilledOutcome), r => r.DestinationId.ShouldBe(801));
    }

    // A rolled-back confirmation filled nothing: the resource is reported Failed (the row flip rolls back with the transaction).
    [Fact]
    public async Task RolledBackAfterAFill_FilledResourceReportedFailed()
    {
        var fill = FillScenario(FillPlaintext, throwAfterAction: true);
        fill.Variables.Setup(v => v.FillEmptySensitiveValueAsync(It.IsAny<int>(), It.IsAny<VariableIdentity>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(SecretSlotState.Filled);

        var result = await fill.Harness.Sut.ConfirmAsync(fill.Harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Failed);
        Outcome(fill.Harness, fill.Nodes.VariableOne).ShouldSatisfyAllConditions(o => o.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Failed), o => o.DestinationId.ShouldBeNull());
    }

    // ── Scenario ───────────────────────────────────────────────────────────

    private sealed record FillHarness(TestHarness Harness, ScenarioNodes Nodes, Mock<IVariableService> Variables, List<OctopusImportSourceMapRow> Upserted, string RowKey);

    // The project and its variable set were imported by an API import (prior rows); its ApiKey secret was withheld (Missing row on
    // the set). The incoming file carries the value encrypted with the export password; the graph's installation id is the space's.
    private static FillHarness FillScenario(string fileValue, string graphInstallationId = InstallationId, string rowOutcome = OctopusImportSourceMapQueries.MissingOutcome, string password = FillPassword, OctopusImportPreviewAction action = OctopusImportPreviewAction.FillMissingSecret, bool throwAfterAction = false)
    {
        var nodes = BuildRichNodes();
        var variable = nodes.VariableOne.GetSource<OctopusVariableDto>();
        variable.Type = "Sensitive";
        variable.IsSensitive = true;
        variable.Value = fileValue == null ? null : OctopusExportTestCipher.Encrypt(fileValue, FillPassword);

        var rowKey = OctopusImportSecretKeys.ForVariable(nodes.VariableSet.SourceId, nodes.VariableSet.OwnerProjectId, variable, (_, _) => null);
        var rows = new List<(string, string, int, string)> { (nameof(OctopusResourceKind.Project), nodes.Project.SourceId, 1001, "Created"), (nameof(OctopusResourceKind.VariableSet), nodes.VariableSet.SourceId, 1002, "Created") };

        if (rowOutcome != null) rows.Add((OctopusImportSourceMapQueries.VariableSecretSourceType, rowKey, 1002, rowOutcome));

        var upserted = new List<OctopusImportSourceMapRow>();
        var variables = new Mock<IVariableService>();
        variables.Setup(v => v.GetSensitiveVariableSlotsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync([new SensitiveVariableSlot(9001, 1002, VariableIdentity.Of("ApiKey", VariableType.Password, []), false)]);

        var resources = new[] { nodes.Project, nodes.VariableSet, nodes.VariableOne };
        var preview = Plan((nodes.Project, OctopusImportPreviewAction.AlreadyImported, 1001), (nodes.VariableSet, OctopusImportPreviewAction.AlreadyImported, 1002), (nodes.VariableOne, action, null));
        var harness = CreateHarness(nodes, BuildDependencyPlan(resources), preview, true, throwAfterAction, false, sourceMap: SourceMap(PriorWith(rows), upserted: upserted).Object, secretsAccessor: FileSession(password), variableService: variables.Object, externalFeedService: Mock.Of<IExternalFeedService>(), graphInstallationId: graphInstallationId);

        return new FillHarness(harness, nodes, variables, upserted, OctopusImportPriorMappings.Normalize(rowKey));
    }

    private static OctopusImportPriorMappings PriorWith(IEnumerable<(string Type, string Id, int DestinationId, string Outcome)> rows)
        => new(rows.ToDictionary(r => OctopusImportPriorMappings.Key(r.Type, r.Id), r => new OctopusImportPriorMapping(r.DestinationId, r.Outcome, null, null)), InstallationId);

    private static IOctopusExportSecretsAccessor FileSession(string password)
        => new OctopusExportSecretsAccessor { Secrets = OctopusExportSecrets.FromPassword(password) };

    private static List<OctopusImportSourceMapRow> SecretRows(IEnumerable<OctopusImportSourceMapRow> rows)
        => rows.Where(r => OctopusImportSourceMapQueries.SecretSourceTypes.Contains(r.SourceType)).ToList();
}

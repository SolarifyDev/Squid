using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Secrets the Octopus API withholds, end to end on the real database: the server import commits with them empty, lists them
/// (names only), and records the fillable ones as <c>Missing</c> rows; no later import — API or file, in either order —
/// ever overwrites a value Squid holds, including one a user set after the first import.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    private const string SecretVariableId = OctopusWithheldSecretResources.SecretVariableId;
    private const string LibrarySecretVariableId = OctopusWithheldSecretResources.LibrarySecretVariableId;
    private const string SecretFeedId = OctopusWithheldSecretResources.SecretFeedId;
    private const string SecretLibraryId = OctopusWithheldSecretResources.SecretLibraryId;

    // The graph (and preview) name a variable by its owner: the project's or the library variable set's id, then its Octopus id.
    private const string SecretVariableSourceId = "Projects-1/variable/" + SecretVariableId;
    private const string LibrarySecretVariableSourceId = SecretLibraryId + "/variable/" + LibrarySecretVariableId;

    [Fact]
    public async Task WithheldSecrets_ServerImportCommits_ListsThemAndRecordsFillableOnesMissing_RepeatImportChangesNothing()
    {
        await using var octopus = StubOctopusServer.Start(ApiKey).WithWithheldSecrets();

        var first = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(first);
        first.PreviewPlan.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretsWithheldByServer && d.Message.Contains("5 secret(s)") && d.Message.Contains("3 of them"), Describe(first.PreviewPlan));

        (await VariableAsync("Db.Password")).ShouldSatisfyAllConditions(v => v.IsSensitive.ShouldBeTrue(), v => string.IsNullOrEmpty(v.Value).ShouldBeTrue());
        (await VariableAsync("Smtp.Password")).ShouldSatisfyAllConditions(v => v.IsSensitive.ShouldBeTrue(), v => string.IsNullOrEmpty(v.Value).ShouldBeTrue());
        (await SingleAsync<ExternalFeed>()).ShouldSatisfyAllConditions(f => f.Username.ShouldBeNull("a username without its password would be a half-configured feed"), f => f.Password.ShouldBeNullOrEmpty());

        var missing = first.Confirm.Data.Session.Result.MissingSensitiveValues;
        missing.Select(m => $"{m.Kind}|{m.Name}|{m.OwnerName}|{m.Field}|{m.Fillable}").Order().ShouldBe(
        [
            "AccountSecret|Cluster token||Token|False",
            "FeedPassword|Private registry||Password|True",
            "SensitiveActionProperty|Notify|Server project|Notify.ApiKey|False",
            "SensitiveVariableValue|Db.Password|Server project|Value|True",
            "SensitiveVariableValue|Smtp.Password|Shared mail|Value|True"
        ], ignoreOrder: true);
        missing.Single(m => m.Name == "Db.Password").ScopeSummary.ShouldBe("Environment: Server TEST");

        var rows = await SecretRowsAsync();
        rows.Select(r => (r.SourceType, r.OutcomeState)).Order().ShouldBe([("FeedSecret", "Missing"), ("VariableSecret", "Missing"), ("VariableSecret", "Missing")]);
        rows.Single(r => r.SourceType == "FeedSecret").DestinationId.ShouldBe((await SingleAsync<ExternalFeed>()).Id);
        rows.Where(r => r.SourceType == "VariableSecret").Select(r => r.DestinationId).Order().ShouldBe(new[] { (await VariableAsync("Db.Password")).VariableSetId, (await VariableAsync("Smtp.Password")).VariableSetId }.Order());

        var before = await CountRowsAsync(ScopeTables);
        var second = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(second);
        second.Confirm.Data.Session.Result.MissingSensitiveValues.ShouldBeEmpty("nothing was written, so nothing is newly missing");
        second.PreviewPlan.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretsWithheldByServer);
        (await CountRowsAsync(ScopeTables)).ShouldBe(before);
        (await SecretRowsAsync()).Select(r => (r.SourceType, r.SourceId, r.DestinationId, r.OutcomeState)).ShouldBe(rows.Select(r => (r.SourceType, r.SourceId, r.DestinationId, r.OutcomeState)), ignoreOrder: true);
    }

    // Values a user sets in Squid after the API import are never touched by a later API import.
    [Fact]
    public async Task WithheldSecrets_SetInSquidAfterTheImport_RepeatServerImportKeepsThem()
    {
        await using var octopus = StubOctopusServer.Start(ApiKey).WithWithheldSecrets();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        await SetInSquidAsync("Db.Password", "set-by-user", feedUsername: "user-robot", feedPassword: "user-token");

        var again = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(again);
        (await VariableAsync("Db.Password")).Value.ShouldBe("set-by-user");
        (await SingleAsync<ExternalFeed>()).ShouldSatisfyAllConditions(f => f.Username.ShouldBe("user-robot"), f => f.Password.ShouldBe("user-token"));
    }

    // File first: the export carried the real values; the API, which has none, adds nothing and records nothing missing.
    [Fact]
    public async Task FileThenServer_SecretsFromTheFileKept_NoMissingRows()
    {
        await using var octopus = StubOctopusServer.Start(ApiKey).WithWithheldSecrets();
        var archive = ExportArchive(extra: SecretDocuments());

        ShouldHaveSucceeded(await ImportFromFileAsync(archive));
        var imported = (await VariableAsync("Db.Password")).Value;

        var fromServer = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(fromServer);
        (await VariableAsync("Db.Password")).Value.ShouldBe(imported);
        imported.ShouldNotBeNullOrEmpty();
        (await SecretRowsAsync()).ShouldBeEmpty();
        fromServer.Confirm.Data.Session.Result.MissingSensitiveValues.ShouldBeEmpty();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private Task<Variable> VariableAsync(string name)
        => Run<SquidDbContext, Variable>(db => db.Set<Variable>().AsNoTracking().SingleAsync(v => v.Name == name));

    private async Task<List<OctopusImportSourceMap>> SecretRowsAsync()
        => (await SourceMapAsync()).Where(r => OctopusImportSourceMapQueries.SecretSourceTypes.Contains(r.SourceType)).ToList();

    private Task SetInSquidAsync(string variableName, string value, string feedUsername, string feedPassword)
        => Run<SquidDbContext>(async db =>
        {
            await db.Set<Variable>().Where(v => v.Name == variableName).ExecuteUpdateAsync(s => s.SetProperty(v => v.Value, value));
            await db.Set<ExternalFeed>().ExecuteUpdateAsync(s => s.SetProperty(f => f.Username, feedUsername).SetProperty(f => f.Password, feedPassword));
        });

    // The same configuration as an export file carries it: values encrypted with the export password.
    private static IEnumerable<(string Id, string DocumentType, string Json)> SecretDocuments() => OctopusWithheldSecretResources.ExportSecretDocuments(ExportPassword);
}

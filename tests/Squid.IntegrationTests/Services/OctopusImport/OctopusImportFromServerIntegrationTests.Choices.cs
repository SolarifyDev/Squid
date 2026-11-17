using System.Net;
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
/// The user's decisions on what a re-import offers, through the real mediator flow the UI drives (preview, validate with the selection,
/// confirm): a ticked secret the server withholds is added empty and recorded missing; the last validation is what is imported; and an
/// old client that knows nothing of choices gets the server defaults.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    private const string AddedSecretId = "6a1d2c3b-0000-4000-8000-000000000002";
    private const string AddedPlainId = "6a1d2c3b-0000-4000-8000-000000000003";

    [Fact]
    public async Task SecretAddedInOctopus_Ticked_AddedEmpty_RecordedMissing_NextImportAlreadyImported()
    {
        await using var octopus = OneDeployment();
        octopus.Resources["projects/Projects-1/variables"] = ProjectVariables(Greeting());
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        octopus.Resources["projects/Projects-1/variables"] = ProjectVariables(Greeting(), new Dictionary<string, object> { ["Id"] = AddedSecretId, ["Name"] = "Api.Key", ["Value"] = null, ["Type"] = "Sensitive", ["IsSensitive"] = true });

        var ticked = await ImportWithChoicesAsync(octopus, plan => Tick(plan, AddedSecretId, true));

        ShouldHaveSucceeded(ticked);
        (await Run<SquidDbContext, Variable>(db => db.Set<Variable>().AsNoTracking().SingleAsync(v => v.Name == "Api.Key"))).ShouldSatisfyAllConditions(v => v.IsSensitive.ShouldBeTrue(), v => string.IsNullOrEmpty(v.Value).ShouldBeTrue());
        ticked.Confirm.Data.Session.Result.MissingSensitiveValues.ShouldContain(m => m.Name == "Api.Key" && m.Fillable, "a withheld value is reported missing, as on a first import");
        (await SecretRowsAsync()).ShouldContain(r => r.SourceType == OctopusImportSourceMapQueries.VariableSecretSourceType && r.OutcomeState == OctopusImportSourceMapQueries.MissingOutcome);

        var again = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(again);
        again.PreviewPlan.Resources.Single(r => r.SourceId == AddedSecretId).ShouldSatisfyAllConditions(r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported, Describe(again.PreviewPlan)), r => r.Choice.ShouldBeNull());
    }

    // Filled from the file, then deleted in Squid, then added back empty by an API import the user ticked: the re-created variable is
    // missing again, so the next export-file import fills it, as the ticked import's preview promised. A settled row of the deleted
    // variable (same variable set, same key) never stands in for the new, empty one.
    [Fact]
    public async Task SecretFilledThenDeletedInSquid_TickedBackByApi_NextFileFillsIt()
    {
        await using var octopus = await MatrixServerAsync();
        var archive = MatrixExport();
        ShouldHaveSucceeded(await ImportFromAsync(ApiSource, octopus, archive, interrupt: false));
        ShouldHaveSucceeded(await ImportFromAsync(FileSource, octopus, archive, interrupt: false));
        (await DbPasswordAsync()).ShouldBe(OctopusWithheldSecretResources.DbPassword);
        await SaveProjectVariableSetInSquidAsync(keepDbPassword: false);

        var ticked = await ImportWithChoicesAsync(octopus, plan => Tick(plan, SecretVariableId, true));

        ShouldHaveSucceeded(ticked);
        (await DbPasswordAsync()).ShouldBeNullOrEmpty("the API withholds the value, so the ticked variable is added empty");
        ticked.Confirm.Data.Session.Result.MissingSensitiveValues.ShouldContain(m => m.Name == "Db.Password" && m.Fillable);
        (await SecretRowsAsync()).Where(r => r.SourceType == OctopusImportSourceMapQueries.VariableSecretSourceType).Select(r => r.OutcomeState).ShouldContain(OctopusImportSourceMapQueries.MissingOutcome);

        var file = await ImportFromAsync(FileSource, octopus, archive, interrupt: false);

        ShouldHaveSucceeded(file);
        file.PreviewPlan.Resources.Single(r => r.SourceId == SecretVariableId).PreviewAction.ShouldBe(OctopusImportPreviewAction.FillMissingSecret, Describe(file.PreviewPlan));
        (await DbPasswordAsync()).ShouldBe(OctopusWithheldSecretResources.DbPassword);
        (await VariableAsync(AddedInSquid)).Value.ShouldBe("user value");
    }

    // The user ticked, validated, then unticked and validated again before importing: the second validation is what is imported (and the
    // other way round). Every validation of a Validated session replaces the plan confirmation imports.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SelectionChangedAfterValidating_TheLastValidationIsImported(bool first, bool last)
    {
        await using var octopus = OneDeployment();
        octopus.Resources["projects/Projects-1/variables"] = ProjectVariables(Greeting());
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        octopus.Resources["projects/Projects-1/variables"] = ProjectVariables(Greeting(), Plain(AddedPlainId, "Added"));

        var result = await Run<IMediator, OctopusImportFlowResult>(async mediator =>
        {
            var (sessionId, fetch, preview) = await PreviewFromServerAsync(mediator, octopus);
            (await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, Posted(Tick(preview, AddedPlainId, first)))).Code.ShouldBe(HttpStatusCode.OK);
            var revalidated = await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, Posted(Tick(preview, AddedPlainId, last)));
            revalidated.Code.ShouldBe(HttpStatusCode.OK);
            revalidated.Data.PreviewPlan.Summary.Choices.Single().Selected.ShouldBe(last ? 1 : 0, "the counts follow the selection");

            return new OctopusImportFlowResult(sessionId, fetch, preview, await OctopusImportHandlerFlow.ConfirmAsync(mediator, SpaceId, sessionId));
        }, OpenAuthorization);

        ShouldHaveSucceeded(result);
        (await NamesAsync<Variable>(v => v.Name)).Contains("Added").ShouldBe(last, "the selection of the last validation is what is imported");
    }

    // A client that predates choices posts rows without them: the server's defaults apply (a channel added in Octopus is created with its
    // history, a variable added in Octopus is not written), exactly as before.
    [Fact]
    public async Task OldClientPlanWithoutChoices_ServerDefaultsApply()
    {
        await using var octopus = OneDeployment();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        AddOctopusHotfixChannelWithHistory(octopus);
        octopus.Resources["projects/Projects-1/variables"] = ProjectVariables(Plain(AddedPlainId, "Added"));

        var result = await ImportWithChoicesAsync(octopus, WithoutChoices);

        ShouldHaveSucceeded(result);
        (await ChannelNamesAsync((await SingleAsync<Project>()).Id)).ShouldContain(HotfixChannel);
        (await Run<SquidDbContext, int>(db => db.Set<Release>().CountAsync(r => r.Version == "2.0.0"))).ShouldBe(1);
        (await NamesAsync<Variable>(v => v.Name)).ShouldNotContain("Added");
    }

    // ── Flow ───────────────────────────────────────────────────────────────

    private Task<OctopusImportFlowResult> ImportWithChoicesAsync(StubOctopusServer octopus, Func<OctopusImportPreviewPlanDto, OctopusImportPreviewPlanDto> choose)
        => Run<IMediator, OctopusImportFlowResult>(async mediator =>
        {
            var (sessionId, fetch, preview) = await PreviewFromServerAsync(mediator, octopus);
            var validate = await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, Posted(choose(preview)));

            if (validate.Code != HttpStatusCode.OK) throw new InvalidOperationException($"Validate refused: {OctopusImportHandlerFlow.DescribeBlockers(validate.Data.Validation.Diagnostics)}");

            return new OctopusImportFlowResult(sessionId, fetch, preview, await OctopusImportHandlerFlow.ConfirmAsync(mediator, SpaceId, sessionId));
        }, OpenAuthorization);

    private static async Task<(Guid SessionId, OctopusHistoryFetchProgressDto Fetch, OctopusImportPreviewPlanDto Preview)> PreviewFromServerAsync(IMediator mediator, StubOctopusServer octopus)
    {
        var sessionId = (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, ApiKey)).Data.Session.SessionId;
        var fetch = await OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey);

        (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, sessionId)).Code.ShouldBe(HttpStatusCode.OK);

        return (sessionId, fetch, (await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId)).Data.PreviewPlan);
    }

    // The UI posts the plan without its history rows (the server owns them).
    private static OctopusImportPreviewPlanDto Posted(OctopusImportPreviewPlanDto plan)
        => new() { GeneratedAt = plan.GeneratedAt, Diagnostics = plan.Diagnostics, RequiredInputs = plan.RequiredInputs, FillPairing = plan.FillPairing, Resources = plan.Resources.Where(r => r.SourceType is not ("Release" or "Deployment" or "ServerTask")).ToList() };

    private static OctopusImportPreviewPlanDto Tick(OctopusImportPreviewPlanDto plan, string sourceId, bool selected)
    {
        plan.Resources.Single(r => r.SourceId == sourceId).Choice.Selected = selected;

        return plan;
    }

    private static OctopusImportPreviewPlanDto WithoutChoices(OctopusImportPreviewPlanDto plan)
    {
        foreach (var row in plan.Resources) row.Choice = null;

        plan.Summary = null;

        return plan;
    }

    private static Dictionary<string, object> Greeting() => Plain("6a1d2c3b-0000-4000-8000-000000000001", "Greeting");

    private static Dictionary<string, object> Plain(string id, string name) => new() { ["Id"] = id, ["Name"] = name, ["Value"] = $"{name} value", ["Type"] = "String" };
}

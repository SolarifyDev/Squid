using System.Linq;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>Withheld secrets at confirmation: the <c>Missing</c> source-map rows a later file import fills, and the result's list.</summary>
public partial class OctopusImportConfirmationOrchestratorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreatedVariableWithAWithheldValue_RecordsAMissingRowOnItsVariableSet_OnlyInAServerSession(bool serverSession)
    {
        var nodes = BuildRichNodes();
        WithholdValue(nodes.VariableOne);
        var upserted = new List<OctopusImportSourceMapRow>();
        var harness = CreateHarness(nodes, BuildDependencyPlan(nodes.RichOrderedResources), BuildPreviewPlan(nodes.RichOrderedResources, reusedResources: [nodes.Environment]), true, false, false, sourceMap: SourceMap(OctopusImportPriorMappings.Empty, upserted: upserted).Object, secretsAccessor: Session(serverSession));

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);

        var rows = upserted.Where(r => OctopusImportSourceMapQueries.SecretSourceTypes.Contains(r.SourceType)).ToList();

        if (!serverSession)
        {
            rows.ShouldBeEmpty();
            harness.SessionService.RecordedResult.MissingSensitiveValues.ShouldBeEmpty();
            return;
        }

        var row = rows.ShouldHaveSingleItem();
        row.SourceType.ShouldBe(OctopusImportSourceMapQueries.VariableSecretSourceType);
        row.SourceId.ShouldBe(OctopusImportSecretKeys.ForVariable("VariableSets-1", nodes.VariableOne.GetSource<OctopusVariableDto>(), (_, _) => null));
        row.DestinationId.ShouldBe(1002, customMessage: "the row points at the Squid variable set, which survives every save; variable ids do not");
        row.OutcomeState.ShouldBe(OctopusImportSourceMapQueries.MissingOutcome);

        var missing = harness.SessionService.RecordedResult.MissingSensitiveValues.ShouldHaveSingleItem();
        missing.Kind.ShouldBe(OctopusImportRequiredInputKind.SensitiveVariableValue);
        missing.Name.ShouldBe("ApiKey");
        missing.Field.ShouldBe("Value");
        missing.OwnerName.ShouldBe("App");
        missing.OwnerKind.ShouldBe("Project");
        missing.DestinationOwnerId.ShouldBe(1001);
        missing.Fillable.ShouldBeTrue();
    }

    // A rolled-back confirmation wrote nothing, so it lists nothing as missing.
    [Fact]
    public async Task RolledBack_ListsNoMissingSensitiveValues()
    {
        var nodes = BuildRichNodes();
        WithholdValue(nodes.VariableOne);
        var harness = CreateHarness(nodes, BuildDependencyPlan(nodes.RichOrderedResources), BuildPreviewPlan(nodes.RichOrderedResources, reusedResources: [nodes.Environment]), true, throwAfterAction: true, false, sourceMap: SourceMap(OctopusImportPriorMappings.Empty).Object, secretsAccessor: Session(true));

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Failed);
        harness.SessionService.RecordedResult.MissingSensitiveValues.ShouldBeEmpty();
    }

    // Session results and preview plans are redacted on every read: the list must survive it (it carries names, never values).
    [Fact]
    public void MissingSensitiveValuesAndWithheldInputs_SurviveTheRedactionEveryReadApplies()
    {
        var result = new OctopusImportSessionResultDto
        {
            MissingSensitiveValues = [new OctopusImportMissingSensitiveValueDto { Kind = OctopusImportRequiredInputKind.FeedPassword, Name = "Registry token", OwnerName = "Web", Field = "Password", ScopeSummary = "Environment: Production", DestinationId = 5, Fillable = true }],
            Resources = [new OctopusImportResourceResultDto { SourceId = "Accounts-1", RequiredInputs = [new OctopusImportRequiredInputDto { Kind = OctopusImportRequiredInputKind.AccountSecret, Name = "Cluster token", FieldName = "Token", WithheldByServer = true }] }]
        };

        var redacted = OctopusImportRedaction.RedactDto(result);

        var listed = redacted.MissingSensitiveValues.ShouldHaveSingleItem();
        (listed.Kind, listed.Name, listed.OwnerName, listed.Field, listed.ScopeSummary, listed.DestinationId, listed.Fillable).ShouldBe((OctopusImportRequiredInputKind.FeedPassword, "Registry token", "Web", "Password", "Environment: Production", (int?)5, true));
        var input = redacted.Resources.Single().RequiredInputs.Single();
        (input.Kind, input.Name, input.FieldName, input.WithheldByServer).ShouldBe((OctopusImportRequiredInputKind.AccountSecret, "Cluster token", "Token", true));
    }

    // The very REST shape: sensitive, no value, the normaliser's marker on the variable.
    private static void WithholdValue(OctopusResourceNode variable)
    {
        var source = variable.GetSource<OctopusVariableDto>();

        source.Type = "Sensitive";
        source.IsSensitive = true;
        source.Value = null;
        source.ExtensionData[OctopusImportWithheldSecrets.Property] = JsonSerializer.SerializeToElement(new[] { "Value" });
    }

    private static IOctopusExportSecretsAccessor Session(bool serverSession)
        => new OctopusExportSecretsAccessor { Secrets = serverSession ? OctopusExportSecrets.WithheldByServer : OctopusExportSecrets.None };
}

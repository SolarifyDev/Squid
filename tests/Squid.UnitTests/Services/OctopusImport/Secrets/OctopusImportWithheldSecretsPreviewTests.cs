using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Secrets;

/// <summary>
/// Secrets the Octopus server API withholds, driven through the REAL normaliser (REST shapes) and the REAL preview planner,
/// validator and variable mapper. A server session reports them (Warning, listed, never required); the very same documents
/// in a file session keep blocking — the marker alone never lowers a severity.
/// </summary>
public class OctopusImportWithheldSecretsPreviewTests
{
    private const string VariableId = "c9e2a6a4-61f6-4bb4-9a43-3c4f2b8a1d10";

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task WithheldVariableValue_WarnsInAServerSession_BlocksOtherwise(bool serverSession, bool expectBlocked)
    {
        var plan = await WebProject(OctopusImportScenario.RestSecret(VariableId, "Db.Password", OctopusImportScenario.Scope("Environment", "Environments-1"))).PlanAsync(serverSession ? OctopusExportSecrets.WithheldByServer : OctopusExportSecrets.None);

        plan.BlocksConfirmation.ShouldBe(expectBlocked);

        var variable = plan.Resource(VariableId);
        var input = variable.RequiredInputs.Single();

        input.Kind.ShouldBe(OctopusImportRequiredInputKind.SensitiveVariableValue);
        input.IsRequired.ShouldBe(expectBlocked);
        input.WithheldByServer.ShouldBe(serverSession);
        input.Fillable.ShouldBeTrue();
        variable.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretWithheldByServer && d.Severity == OctopusImportCompatibilitySeverity.Warning).ShouldBe(serverSession);
        plan.Blockers.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing).ShouldBe(expectBlocked);
    }

    [Fact]
    public async Task ExportPasswordUnreadable_WithheldMarkerStillBlocks()
    {
        var plan = await WebProject(OctopusImportScenario.RestSecret(VariableId, "Db.Password")).PlanAsync(OctopusExportSecrets.Unreadable);

        plan.BlocksConfirmation.ShouldBeTrue();
        plan.Resource(VariableId).RequiredInputs.Single().WithheldByServer.ShouldBeFalse();
    }

    // The validator rebuilds every sensitive input itself: a client posting IsRequired=false cannot lift a file blocker.
    [Fact]
    public async Task Validator_ClientPostedNotRequired_StillBlocksAFileSessionSecret()
    {
        var scenario = WebProject(OctopusImportScenario.RestSecret(VariableId, "Db.Password"));
        var plan = await scenario.PlanAsync(OctopusExportSecrets.None);

        foreach (var input in plan.Preview.RequiredInputs.Concat(plan.Preview.Resources.SelectMany(r => r.RequiredInputs)))
            input.IsRequired = false;

        var validation = new OctopusImportPreviewValidator(new OctopusExportSecretsAccessor { Secrets = OctopusExportSecrets.None }).Validate(plan.Graph, plan.DependencyPlan, plan.Conflicts, plan.Preview);

        validation.Diagnostics.ShouldContain(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker && d.Code == OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing && d.SourceId == VariableId);
    }

    [Theory]
    [InlineData(true, "robot")]
    [InlineData(true, null)]
    [InlineData(false, "robot")]
    public async Task Feed_WithheldPassword_ListedAsFillableAndCredentialsOmitted(bool hasPassword, string username)
    {
        var plan = await new OctopusImportScenario().RestFeed("Feeds-1", "Registry", username, hasPassword).PlanAsync(OctopusExportSecrets.WithheldByServer);

        var feed = plan.Resource("Feeds-1");

        plan.BlocksConfirmation.ShouldBeFalse();
        feed.Diagnostics.Any(d => d.Code == OctopusImportRedactionDiagnosticCodes.FeedCredentialsOmitted).ShouldBe(hasPassword, customMessage: "a feed whose password is withheld has credentials to configure; a username without its password must never be written");
        feed.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretWithheldByServer).ShouldBe(hasPassword);
        feed.RequiredInputs.Select(i => (i.Kind, i.FieldName, i.Fillable, i.IsRequired)).ShouldBe(hasPassword ? [(OctopusImportRequiredInputKind.FeedPassword, "Password", true, false)] : []);
    }

    [Fact]
    public async Task AccountAndActionSecrets_ListedNotFillable_WithTheirFieldNames()
    {
        var plan = await new OctopusImportScenario()
            .RestTokenAccount("Accounts-1", "Cluster token")
            .RestProject("Projects-1", "Web", [], [], OctopusImportScenario.RestScriptActionWithSecret("Actions-1", "Notify.ApiKey"))
            .PlanAsync(OctopusExportSecrets.WithheldByServer);

        var account = plan.Resource("Accounts-1");
        var action = plan.Resource("Actions-1");

        account.RequiredInputs.Select(i => (i.Kind, i.FieldName, i.Fillable)).ShouldBe([(OctopusImportRequiredInputKind.AccountSecret, "Token", false)]);
        action.RequiredInputs.Select(i => (i.Kind, i.FieldName, i.Fillable)).ShouldBe([(OctopusImportRequiredInputKind.SensitiveActionProperty, "Notify.ApiKey", false)]);
        account.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretWithheldByServer).Message.ShouldContain("Missing: Token.");
        action.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretWithheldByServer).Message.ShouldContain("Missing: Notify.ApiKey.");
    }

    // The same REST documents in a file session (a planted marker) are never reported as withheld by the server.
    [Fact]
    public async Task FileSession_WithheldMarkersOnFeedAccountAction_AreNotListed()
    {
        var plan = await new OctopusImportScenario()
            .RestFeed("Feeds-1", "Registry", "robot", hasPassword: true)
            .RestTokenAccount("Accounts-1", "Cluster token")
            .RestProject("Projects-1", "Web", [], [], OctopusImportScenario.RestScriptActionWithSecret("Actions-1", "Notify.ApiKey"))
            .PlanAsync(OctopusExportSecrets.None);

        plan.Preview.Resources.SelectMany(r => r.RequiredInputs).ShouldBeEmpty();
        plan.AllDiagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretWithheldByServer || d.Code == OctopusImportPreviewDiagnosticCodes.SecretsWithheldByServer);
    }

    [Fact]
    public async Task Summary_CountsEveryWithheldSecretOnWrittenResources_AndHowManyAFileCanFill()
    {
        var plan = await new OctopusImportScenario()
            .Environment("Environments-1", "Production")
            .RestFeed("Feeds-1", "Registry", "robot", hasPassword: true)
            .RestTokenAccount("Accounts-1", "Cluster token")
            .RestProject("Projects-1", "Web", [], [OctopusImportScenario.RestSecret(VariableId, "Db.Password")], OctopusImportScenario.RestScriptActionWithSecret("Actions-1", "Notify.ApiKey"))
            .PlanAsync(OctopusExportSecrets.WithheldByServer);

        var summary = plan.Preview.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretsWithheldByServer);

        summary.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
        summary.Message.ShouldContain("4 secret(s)");
        summary.Message.ShouldContain("2 of them");
        summary.Message.ShouldContain("set the other 2 in Squid");
        plan.BlocksConfirmation.ShouldBeFalse();
    }

    // A repeat import writes nothing for an already imported feed or project, so it lists none of their secrets again.
    [Fact]
    public async Task RepeatImport_AlreadyImportedResources_ListNoSecrets()
    {
        var scenario = new OctopusImportScenario()
            .Environment("Environments-1", "Production")
            .RestFeed("Feeds-1", "Registry", "robot", hasPassword: true)
            .RestProject("Projects-1", "Web", [], [OctopusImportScenario.RestSecret(VariableId, "Db.Password")], OctopusImportScenario.RestScriptActionWithSecret("Actions-1", "Notify.ApiKey"));
        var first = await scenario.PlanAsync(OctopusExportSecrets.WithheldByServer);

        var repeat = await scenario.ImportedAs(first).PlanAsync(OctopusExportSecrets.WithheldByServer);

        repeat.Preview.Resources.SelectMany(r => r.RequiredInputs).ShouldBeEmpty();
        repeat.AllDiagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretsWithheldByServer);
        repeat.BlocksConfirmation.ShouldBeFalse();
    }

    [Theory]
    [InlineData(true, OctopusImportCompatibilitySeverity.Warning, OctopusImportVariableMappingDiagnosticCodes.SensitiveValueWithheldByServer)]
    [InlineData(false, OctopusImportCompatibilitySeverity.Blocker, OctopusImportVariableMappingDiagnosticCodes.SensitiveValueOmitted)]
    public async Task VariableMapper_WithheldValue_WrittenEmptyAndSensitive_SeverityFromTheSession(bool serverSession, OctopusImportCompatibilitySeverity severity, string code)
    {
        var graph = await WebProject(OctopusImportScenario.RestSecret(VariableId, "Db.Password")).BuildGraphAsync();
        var set = graph.Resources.Single(r => r.Kind == OctopusResourceKind.VariableSet);
        var idMap = new OctopusImportIdMap();
        idMap.AddCreated(graph.Resources.Single(r => r.Kind == OctopusResourceKind.Project), 41);
        var mapper = new OctopusImportVariableMapper(new OctopusExportSecretsAccessor { Secrets = serverSession ? OctopusExportSecrets.WithheldByServer : OctopusExportSecrets.None });

        var mapping = mapper.MapToUpdateCommand(set, idMap, 90, 7);

        var written = mapping.UpdateCommand.Variables.Single();
        written.Value.ShouldBe(string.Empty);
        written.IsSensitive.ShouldBeTrue();
        mapping.Diagnostics.Single().Severity.ShouldBe(severity);
        mapping.Diagnostics.Single().Code.ShouldBe(code);
        mapping.RequiredInputs.Single().IsRequired.ShouldBe(!serverSession);
    }

    private static OctopusImportScenario WebProject(object variable)
        => new OctopusImportScenario().Environment("Environments-1", "Production").RestProject("Projects-1", "Web", [], [variable]);
}

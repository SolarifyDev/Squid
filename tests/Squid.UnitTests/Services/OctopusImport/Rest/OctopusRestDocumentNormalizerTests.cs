using System.Linq;
using System.Text.Json.Nodes;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

public class OctopusRestDocumentNormalizerTests
{
    private const string Marker = OctopusRestDocumentNormalizer.WithheldSecretsProperty;

    [Fact]
    public void ReservedNames_Pinned()
    {
        // WP6 reads the marker back and the export pipeline already owns the Squid.Import. prefix; renaming either drops every withheld secret.
        OctopusRestDocumentNormalizer.WithheldSecretsProperty.ShouldBe("Squid.Import.WithheldSecrets");
        OctopusRestDocumentNormalizer.ReservedPropertyPrefix.ShouldBe("Squid.Import.");
    }

    // ── Common ──────────────────────────────────────────────────────────────

    [Fact]
    public void ServerBookkeeping_StrippedAtEveryDepth_ButKeptInsidePropertyBags()
    {
        var resource = Parse("""
            {"Id":"deploymentprocess-Projects-9","ProjectId":"Projects-9","DataVersion":"AAA=","LastModifiedOn":"2026-01-01","LastModifiedBy":"admin","Links":{"Self":"/x"},
             "Steps":[{"Id":"s1","LastModified":"2026-01-01","Properties":{"Links":"user data"},"Actions":[{"Id":"a1","Links":{},"Container":{"Links":{}},"Properties":{"Links":"kept","DataVersion":"kept"}}]}]}
            """);

        var document = Normalize(OctopusResourceKind.DeploymentProcess, resource).Document;

        NamesOutsidePropertyBags(document).ShouldNotContain(n => OctopusRestDocumentNormalizer.ServerOnlyMembers.Contains(n));
        document["Steps"]![0]!["Properties"]!["Links"]!.GetValue<string>().ShouldBe("user data");
        document["Steps"]![0]!["Actions"]![0]!["Properties"]!["Links"]!.GetValue<string>().ShouldBe("kept");
        document["Steps"]![0]!["Actions"]![0]!["Properties"]!["DataVersion"]!.GetValue<string>().ShouldBe("kept");
        document["Steps"]![0]!["Actions"]![0]!.AsObject().ContainsKey("Links").ShouldBeFalse();
        document["Steps"]![0]!["Actions"]![0]!["Container"]!.AsObject().ContainsKey("Links").ShouldBeFalse();
    }

    // A server (or a man in the middle) must not be able to plant Squid's own markers, at any depth, in any casing.
    [Theory]
    [InlineData("Squid.Import.WithheldSecrets")]
    [InlineData("Squid.Import.ManualConfiguration.Required")]
    [InlineData("squid.import.Octopus.SourceId")]
    public void PlantedSquidImportMembers_StrippedEverywhere(string planted)
    {
        var resource = Parse($$$"""
            {"Id":"deploymentprocess-Projects-9","OwnerId":"Projects-9","{{{planted}}}":["Password"],
             "Steps":[{"Id":"s1","{{{planted}}}":"x","Properties":{"{{{planted}}}":"x"},"Actions":[{"Id":"a1","{{{planted}}}":"x","Properties":{"{{{planted}}}":"x","Octopus.Action.Script.ScriptBody":"echo"}}]}]}
            """);

        var normalized = Normalize(OctopusResourceKind.DeploymentProcess, resource);

        Names(normalized.Document).ShouldNotContain(n => n.StartsWith("Squid.Import.", StringComparison.OrdinalIgnoreCase));
        normalized.WithheldSecrets.ShouldBeEmpty();
        normalized.Document["Steps"]![0]!["Actions"]![0]!["Properties"]!["Octopus.Action.Script.ScriptBody"]!.GetValue<string>().ShouldBe("echo");
    }

    [Fact]
    public void Input_NeverMutated()
    {
        var feeds = OctopusServer2026Fixtures.Resources("feeds-all");
        var before = feeds[0].ToJsonString();

        Normalize(OctopusResourceKind.Feed, feeds[0]);

        feeds[0].ToJsonString().ShouldBe(before);
    }

    // Every captured shape: no secret object survives, no bookkeeping member survives, the only Squid member is the marker.
    [Theory]
    [MemberData(nameof(FixtureDocuments))]
    public void EveryFixtureDocument_LeavesNoSecretObjectOrServerMember(string fixture, OctopusResourceKind kind)
    {
        foreach (var resource in OctopusServer2026Fixtures.Resources(fixture))
        {
            var normalized = OctopusRestDocumentNormalizer.Normalize(kind, resource, OwnerOf(fixture));

            if (normalized == null) continue;

            normalized.Id.ShouldBe(resource["Id"]!.GetValue<string>().StartsWith("Projects-") && kind == OctopusResourceKind.DeploymentSettings ? $"deploymentsettings-{resource["Id"]}" : resource["Id"]!.GetValue<string>());
            Objects(normalized.Document).ShouldNotContain(o => OctopusRestDocumentNormalizer.IsSensitiveValue(o), customMessage: $"{fixture} {normalized.Id} kept a SensitiveValue object");
            Names(normalized.Document).Where(n => n.StartsWith("Squid.", StringComparison.OrdinalIgnoreCase)).ShouldAllBe(n => n == Marker);
            NamesOutsidePropertyBags(normalized.Document).ShouldNotContain(n => OctopusRestDocumentNormalizer.ServerOnlyMembers.Contains(n));
        }
    }

    // ── Feed ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Feeds-1", true, "registry-user")]
    [InlineData("feeds-docker-hub", true, "hub-user")]
    [InlineData("Feeds-2", false, null)]
    public void Feed_PasswordNulled_WithheldOnlyWhenTheServerHadOne(string feedId, bool withheld, string username)
    {
        var normalized = Normalize(OctopusResourceKind.Feed, Fixture("feeds-all", feedId));

        normalized.Id.ShouldBe(feedId);
        normalized.Document["Password"].ShouldBeNull();
        normalized.Document["Username"]?.GetValue<string>().ShouldBe(username);
        normalized.WithheldSecrets.ShouldBe(withheld ? ["Password"] : []);
        MarkerOf(normalized.Document).ShouldBe(withheld ? ["Password"] : []);
    }

    [Theory]
    [InlineData("feeds-builtin")]
    [InlineData("feeds-builtin-releases")]
    public void BuiltInFeeds_NotImportable(string feedId)
    {
        var feed = Fixture("feeds-all", feedId);

        OctopusRestDocumentNormalizer.IsExcluded(OctopusResourceKind.Feed, feed).ShouldBeTrue();
        OctopusRestDocumentNormalizer.Normalize(OctopusResourceKind.Feed, feed).ShouldBeNull();
    }

    // ── Account ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Accounts-1", "Password", "Username")]
    [InlineData("Accounts-2", "Token", null)]
    [InlineData("Accounts-3", "SecretKey", "AccessKey")]
    [InlineData("Accounts-4", "Password", "ClientId")]
    [InlineData("Accounts-5", "CertificateBytes", "SubscriptionNumber")]
    public void Account_SecretFieldsFoldedIntoCredentials(string accountId, string secret, string plainField)
    {
        var normalized = Normalize(OctopusResourceKind.Account, Fixture("accounts-all", accountId));
        var credentials = normalized.Document["Credentials"]!.AsObject();

        normalized.Document.ContainsKey(secret).ShouldBeFalse();
        credentials.ContainsKey(secret).ShouldBeTrue();
        credentials[secret].ShouldBeNull();
        MarkerOf(credentials).ShouldBe([secret]);
        normalized.WithheldSecrets.ShouldBe([$"Credentials.{secret}"]);

        if (plainField != null) normalized.Document.ContainsKey(plainField).ShouldBeTrue();
    }

    [Fact]
    public void Account_ExportShapedCredentials_LeftAlone()
    {
        var normalized = Normalize(OctopusResourceKind.Account, Parse("""{"Id":"Accounts-9","AccountType":"Token","Credentials":{"Token":"cipher"}}"""));

        normalized.Document["Credentials"]!["Token"]!.GetValue<string>().ShouldBe("cipher");
        normalized.WithheldSecrets.ShouldBeEmpty();
    }

    // ── Team ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""[{"Id":"S-1-5-21-1001","DisplayName":"Deployers","DisplayIdAndName":true}]""", new[] { "S-1-5-21-1001" })]
    [InlineData("""["S-1-5-21-1001"]""", new[] { "S-1-5-21-1001" })]
    [InlineData("[]", new string[0])]
    public void Team_SecurityGroupsBecomeIds(string groups, string[] expected)
    {
        var normalized = Normalize(OctopusResourceKind.Team, Parse($$"""{"Id":"Teams-2","Name":"Directory","ExternalSecurityGroups":{{groups}}}"""));

        normalized.Document["ExternalSecurityGroups"]!.AsArray().Select(g => g!.GetValue<string>()).ShouldBe(expected);
    }

    // ── Variable set ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("variables-variableset-Projects-1", "Project")]
    [InlineData("variables-variableset-LibraryVariableSets-1", "LibraryVariableSet")]
    [InlineData("variables-variableset-LibraryVariableSets-2", "LibraryVariableSet")]
    public void VariableSet_OwnerTypeInjectedFromTheOwner(string fixture, string ownerType)
    {
        var normalized = Normalize(OctopusResourceKind.VariableSet, OctopusServer2026Fixtures.Resources(fixture).Single());

        normalized.Document["OwnerType"]!.GetValue<string>().ShouldBe(ownerType);
    }

    [Fact]
    public void VariableSet_ExistingOwnerTypeAndMissingOwnerId()
    {
        Normalize(OctopusResourceKind.VariableSet, Parse("""{"Id":"variableset-X","OwnerId":"Projects-1","OwnerType":"LibraryVariableSet"}""")).Document["OwnerType"]!.GetValue<string>().ShouldBe("LibraryVariableSet");

        var orphan = OctopusRestDocumentNormalizer.Normalize(OctopusResourceKind.VariableSet, Parse("""{"Id":"variableset-Projects-4"}"""), "Projects-4");

        orphan.Document["OwnerId"]!.GetValue<string>().ShouldBe("Projects-4");
        orphan.Document["OwnerType"]!.GetValue<string>().ShouldBe("Project");
    }

    [Fact]
    public void VariableSet_EverySensitiveValueWithheld_PlainValuesAndPromptsKept()
    {
        var normalized = Normalize(OctopusResourceKind.VariableSet, OctopusServer2026Fixtures.Resources("variables-variableset-Projects-1").Single());
        var variables = normalized.Document["Variables"]!.AsArray();

        normalized.WithheldSecrets.ShouldBe(["Variables[2].Value", "Variables[3].Value", "Variables[4].Value"]);
        variables.Take(2).ShouldAllBe(v => v!["Value"] != null && !v.AsObject().ContainsKey(Marker));
        variables.Skip(2).ShouldAllBe(v => v!["Value"] == null && MarkerOf(v.AsObject()).SequenceEqual(new[] { "Value" }));
    }

    // The API never sends a sensitive value; one that arrives anyway is still not imported, whichever flag marks it.
    [Theory]
    [InlineData("""{"IsSensitive":true,"Type":"Sensitive"}""")]
    [InlineData("""{"IsSensitive":true}""")]
    [InlineData("""{"Type":"Sensitive"}""")]
    [InlineData("""{"Type":"sensitive"}""")]
    public void VariableSet_SensitiveValueThatArrivesAnyway_Nulled(string flags)
    {
        var variable = JsonNode.Parse(flags)!.AsObject();
        variable["Id"] = "v1";
        variable["Name"] = "Secret";
        variable["Value"] = "leaked";

        var normalized = Normalize(OctopusResourceKind.VariableSet, new JsonObject { ["Id"] = "variableset-Projects-1", ["OwnerId"] = "Projects-1", ["Variables"] = new JsonArray(variable) });

        normalized.Document["Variables"]![0]!["Value"].ShouldBeNull();
        normalized.WithheldSecrets.ShouldBe(["Variables[0].Value"]);
    }

    [Fact]
    public void VariableSet_PromptDisplaySettingsObject_Untouched()
    {
        var normalized = Normalize(OctopusResourceKind.VariableSet, OctopusServer2026Fixtures.Resources("variables-variableset-Projects-2").Single());

        normalized.Document["Variables"]![0]!["Prompt"]!["DisplaySettings"]!["Octopus.ControlType"]!.GetValue<string>().ShouldBe("Select");
        normalized.WithheldSecrets.ShouldBe(["Variables[1].Value"]);
    }

    // ── Deployment process ──────────────────────────────────────────────────

    [Theory]
    [InlineData(OctopusResourceKind.DeploymentProcess, "projects-Projects-1-deploymentprocesses", "Projects-1")]
    [InlineData(OctopusResourceKind.DeploymentProcessSnapshot, "deploymentprocesses-deploymentprocess-Projects-1-s-4-ABCDE", "Projects-1")]
    public void Process_ProjectIdBecomesOwnerId(OctopusResourceKind kind, string fixture, string projectId)
    {
        var document = Normalize(kind, OctopusServer2026Fixtures.Resources(fixture).Single()).Document;

        document["OwnerId"]!.GetValue<string>().ShouldBe(projectId);
        document.ContainsKey("ProjectId").ShouldBeFalse();
    }

    [Fact]
    public void Process_SensitiveActionPropertyNulled_NameRecordedOnTheAction()
    {
        const string property = "Octopus.Action.WindowsService.CustomAccountPassword";

        var normalized = Normalize(OctopusResourceKind.DeploymentProcess, OctopusServer2026Fixtures.Resources("projects-Projects-2-deploymentprocesses").Single());
        var action = normalized.Document["Steps"]![0]!["Actions"]![0]!.AsObject();

        action["Properties"]!.AsObject().ContainsKey(property).ShouldBeTrue();
        action["Properties"]![property].ShouldBeNull();
        MarkerOf(action).ShouldBe([property]);
        action["Properties"]!.AsObject().ContainsKey(Marker).ShouldBeFalse();
        normalized.WithheldSecrets.ShouldBe([$"Steps[0].Actions[0].Properties['{property}']"]);
    }

    [Fact]
    public void Process_SensitiveStepPropertyRecordedOnTheStep_EmptySecretNotRecorded()
    {
        var normalized = Normalize(OctopusResourceKind.DeploymentProcess, Parse("""
            {"Id":"deploymentprocess-Projects-9","OwnerId":"Projects-9","Steps":[{"Id":"s1","Properties":{"Step.Secret":{"HasValue":true,"NewValue":null},"Step.Empty":{"HasValue":false,"NewValue":null,"Hint":null}},"Actions":[]}]}
            """));
        var step = normalized.Document["Steps"]![0]!.AsObject();

        MarkerOf(step).ShouldBe(["Step.Secret"]);
        step["Properties"]!["Step.Empty"].ShouldBeNull();
        normalized.WithheldSecrets.ShouldBe(["Steps[0].Properties['Step.Secret']"]);
    }

    // ── Project and deployment settings ────────────────────────────────────

    [Fact]
    public void Project_DeploymentSettingsIdSynthesised_SensitiveTemplateDefaultWithheld()
    {
        var projects = OctopusServer2026Fixtures.Resources("projects-all");
        var plain = Normalize(OctopusResourceKind.Project, projects[0]);
        var templated = Normalize(OctopusResourceKind.Project, projects[1]);

        plain.Document["DeploymentSettingsId"]!.GetValue<string>().ShouldBe("deploymentsettings-Projects-1");
        plain.WithheldSecrets.ShouldBeEmpty();
        templated.Document["DeploymentSettingsId"]!.GetValue<string>().ShouldBe("deploymentsettings-Projects-2");
        templated.Document["Templates"]![0]!["DefaultValue"].ShouldBeNull();
        MarkerOf(templated.Document["Templates"]![0]!.AsObject()).ShouldBe(["DefaultValue"]);
        templated.WithheldSecrets.ShouldBe(["Templates[0].DefaultValue"]);
    }

    [Fact]
    public void Project_ExistingDeploymentSettingsId_Kept()
        => Normalize(OctopusResourceKind.Project, Parse("""{"Id":"Projects-1","DeploymentSettingsId":"deploymentsettings-Projects-1-custom"}""")).Document["DeploymentSettingsId"]!.GetValue<string>().ShouldBe("deploymentsettings-Projects-1-custom");

    [Fact]
    public void DeploymentSettings_CurrentServerBody_KeepsIdAndProject()
    {
        var document = Normalize(OctopusResourceKind.DeploymentSettings, OctopusServer2026Fixtures.Resources("projects-Projects-1-deploymentsettings").Single()).Document;

        document["Id"]!.GetValue<string>().ShouldBe("deploymentsettings-Projects-1");
        document["ProjectId"]!.GetValue<string>().ShouldBe("Projects-1");
        document["ConnectivityPolicy"]!["SkipMachineBehavior"]!.GetValue<string>().ShouldBe("None");
    }

    // Older servers embed the settings on the project: the same call shapes the project body into a settings document.
    [Fact]
    public void DeploymentSettings_FromAnOlderServersProject_IdAndConnectivityPolicyShaped()
    {
        var project = Parse("""{"Id":"Projects-7","Name":"Legacy","ProjectConnectivityPolicy":{"SkipMachineBehavior":"SkipUnavailableMachines"},"DefaultGuidedFailureMode":"Off"}""");

        var document = OctopusRestDocumentNormalizer.Normalize(OctopusResourceKind.DeploymentSettings, project, "Projects-7").Document;

        document["Id"]!.GetValue<string>().ShouldBe("deploymentsettings-Projects-7");
        document["ProjectId"]!.GetValue<string>().ShouldBe("Projects-7");
        document["ConnectivityPolicy"]!["SkipMachineBehavior"]!.GetValue<string>().ShouldBe("SkipUnavailableMachines");
        document.ContainsKey("ProjectConnectivityPolicy").ShouldBeFalse();
    }

    [Fact]
    public void DeploymentSettings_WithoutAProject_Rejected()
        => Should.Throw<ArgumentException>(() => OctopusRestDocumentNormalizer.Normalize(OctopusResourceKind.DeploymentSettings, Parse("""{"Id":"deploymentsettings-x"}""")));

    // ── Other secret holders ────────────────────────────────────────────────

    [Fact]
    public void ActionTemplate_SensitiveParameterDefaultWithheld()
    {
        var normalized = Normalize(OctopusResourceKind.ActionTemplate, OctopusServer2026Fixtures.Resources("actiontemplates-all").Single());

        normalized.Document["Parameters"]![1]!["DefaultValue"].ShouldBeNull();
        normalized.Document["Parameters"]![0]!["DefaultValue"].ShouldNotBeNull();
        normalized.WithheldSecrets.ShouldBe(["Parameters[1].DefaultValue"]);
    }

    [Fact]
    public void Certificate_PrivateMaterialWithheld_NullPasswordNotRecorded()
    {
        var certificates = OctopusServer2026Fixtures.Resources("certificates-all");

        Normalize(OctopusResourceKind.Certificate, certificates[0]).WithheldSecrets.ShouldBe(["CertificateData", "Password"]);
        Normalize(OctopusResourceKind.Certificate, certificates[1]).WithheldSecrets.ShouldBe(["CertificateData"]);
    }

    [Fact]
    public void SensitiveValueInsideAnArray_NulledAndRecorded()
    {
        var normalized = Normalize(OctopusResourceKind.Machine, Parse("""{"Id":"Machines-1","Endpoint":{"Keys":[{"HasValue":true,"NewValue":null},"plain"]}}"""));

        normalized.Document["Endpoint"]!["Keys"]![0].ShouldBeNull();
        normalized.WithheldSecrets.ShouldBe(["Endpoint.Keys[0]"]);
    }

    // An object that merely has a HasValue member among others is data, not a secret.
    [Fact]
    public void ObjectWithExtraMembers_NotTreatedAsASecret()
        => OctopusRestDocumentNormalizer.IsSensitiveValue(Parse("""{"HasValue":true,"NewValue":null,"Other":1}""")).ShouldBeFalse();

    // ── Helpers ─────────────────────────────────────────────────────────────

    public static TheoryData<string, OctopusResourceKind> FixtureDocuments()
    {
        var data = new TheoryData<string, OctopusResourceKind>();

        foreach (var (fixture, kind) in OctopusRestFixtureKinds.All) data.Add(fixture, kind);

        return data;
    }

    private static string OwnerOf(string fixture) => OctopusRestFixtureKinds.OwnerOf(fixture);

    private static OctopusRestNormalizedDocument Normalize(OctopusResourceKind kind, JsonObject resource) => OctopusRestDocumentNormalizer.Normalize(kind, resource).ShouldNotBeNull();

    private static JsonObject Fixture(string name, string id) => OctopusServer2026Fixtures.Resources(name).Single(r => r["Id"]!.GetValue<string>() == id);

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static string[] MarkerOf(JsonObject node) => node[Marker] is JsonArray names ? names.Select(n => n!.GetValue<string>()).ToArray() : [];

    private static IEnumerable<JsonObject> Objects(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;

            foreach (var child in obj.Select(m => m.Value).SelectMany(Objects)) yield return child;
        }

        if (node is JsonArray array)
            foreach (var child in array.SelectMany(Objects)) yield return child;
    }

    private static IEnumerable<string> Names(JsonNode node) => Objects(node).SelectMany(o => o.Select(m => m.Key));

    private static IEnumerable<string> NamesOutsidePropertyBags(JsonNode node)
        => Objects(node).Where(o => o.Parent is not JsonObject parent || !parent.Any(m => m.Key == "Properties" && ReferenceEquals(m.Value, o))).SelectMany(o => o.Select(m => m.Key));
}

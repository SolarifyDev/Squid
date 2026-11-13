using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

/// <summary>
/// Pins the REST facts verified against a real Octopus 2026.1 server, through the sanitised fixtures that later work
/// relies on. A fixture edit that drifts from the real shape fails here instead of silently weakening a seam test.
/// </summary>
public class OctopusServer2026FixtureTests
{
    // The export file appends this suffix to every document id; the REST API never does.
    private static readonly Regex ExportIdSuffix = new("-[0-9A-F]{32}(?=$|/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Fixtures_AreCopiedToTheTestOutput()
    {
        OctopusServer2026Fixtures.Names.Count.ShouldBe(41);
    }

    [Fact]
    public void Ids_NeverCarryTheExportSuffix()
    {
        var ids = OctopusServer2026Fixtures.Names.SelectMany(name => IdValues(OctopusServer2026Fixtures.Node(name))).ToList();

        ids.ShouldNotBeEmpty();
        ids.Where(id => ExportIdSuffix.IsMatch(id)).ShouldBeEmpty();
    }

    [Fact]
    public void SensitiveValueObjects_AppearExactlyWhereOctopusWithholdsSecrets()
    {
        var paths = OctopusServer2026Fixtures.Names.SelectMany(name => SensitiveValuePaths(OctopusServer2026Fixtures.Node(name), name)).Distinct().Order(StringComparer.Ordinal).ToList();

        paths.ShouldBe(new[]
        {
            "accounts-all[].CertificateBytes", "accounts-all[].Password", "accounts-all[].SecretKey", "accounts-all[].Token",
            "actiontemplates-all[].Parameters[].DefaultValue",
            "certificates-all[].CertificateData", "certificates-all[].Password",
            "feeds-all[].Password",
            "projects-all[].Templates[].DefaultValue",
            "projects-Projects-2-deploymentprocesses.Steps[].Actions[].Properties.Octopus.Action.WindowsService.CustomAccountPassword"
        }.Order(StringComparer.Ordinal).ToList());
    }

    [Theory]
    [InlineData("variables-variableset-Projects-1")]
    [InlineData("variables-variableset-Projects-2")]
    [InlineData("variables-variableset-LibraryVariableSets-1")]
    [InlineData("variables-variableset-LibraryVariableSets-2")]
    public void VariableSets_HaveNoOwnerType_AndNeverDiscloseSensitiveValues(string name)
    {
        var set = OctopusServer2026Fixtures.Resources(name).Single();

        set.ContainsKey("OwnerType").ShouldBeFalse();

        foreach (var variable in set["Variables"]!.AsArray().OfType<JsonObject>().Where(v => (bool)v["IsSensitive"]!))
        {
            ((string)variable["Type"]).ShouldBe("Sensitive");
            variable["Value"].ShouldBeNull();
        }
    }

    [Theory]
    [InlineData("Feeds-1", true)]
    [InlineData("Feeds-2", false)]
    [InlineData("feeds-docker-hub", true)]
    public void Feeds_ReportWhetherAPasswordExists_WithoutTheValue(string feedId, bool hasValue)
    {
        var password = Resource("feeds-all", feedId)["Password"]!.AsObject();

        ((bool)password["HasValue"]).ShouldBe(hasValue);
        password["NewValue"].ShouldBeNull();
    }

    [Theory]
    [InlineData("feeds-builtin")]
    [InlineData("feeds-builtin-releases")]
    public void BuiltInFeeds_HaveNoPasswordMember(string feedId)
    {
        Resource("feeds-all", feedId).ContainsKey("Password").ShouldBeFalse();
    }

    [Theory]
    [InlineData("Accounts-1", "Password")]
    [InlineData("Accounts-2", "Token")]
    [InlineData("Accounts-3", "SecretKey")]
    [InlineData("Accounts-4", "Password")]
    [InlineData("Accounts-5", "CertificateBytes")]
    public void Accounts_FlattenSecretsPerType_WithoutCredentialsMember(string accountId, string secretField)
    {
        var account = Resource("accounts-all", accountId);

        account.ContainsKey("Credentials").ShouldBeFalse();
        ((bool)account[secretField]!["HasValue"]).ShouldBeTrue();
    }

    [Theory]
    [InlineData("projects-Projects-1-deploymentprocesses", "Projects-1")]
    [InlineData("deploymentprocesses-deploymentprocess-Projects-1-s-4-ABCDE", "Projects-1")]
    public void DeploymentProcesses_NameTheirProjectAsProjectId_NotOwnerId(string name, string projectId)
    {
        var process = OctopusServer2026Fixtures.Resources(name).Single();

        ((string)process["ProjectId"]).ShouldBe(projectId);
        process.ContainsKey("OwnerId").ShouldBeFalse();
    }

    [Fact]
    public void Projects_HaveNoDeploymentSettingsId_AndAreDatabaseBacked()
    {
        foreach (var project in OctopusServer2026Fixtures.Resources("projects-all"))
        {
            project.ContainsKey("DeploymentSettingsId").ShouldBeFalse();
            ((string)project["PersistenceSettings"]!["Type"]).ShouldBe("Database");
        }
    }

    [Theory]
    [InlineData("users-permissions-unrestricted")]
    [InlineData("users-permissions-restricted")]
    public void PermissionGrants_AlwaysCarryAllFourRestrictionDimensions(string name)
    {
        var grants = OctopusServer2026Fixtures.Resources(name).Single()["SpacePermissions"]!.AsObject().SelectMany(permission => permission.Value!.AsArray().OfType<JsonObject>()).ToList();

        grants.ShouldNotBeEmpty();
        grants.ShouldAllBe(grant => new[] { "RestrictedToProjectIds", "RestrictedToEnvironmentIds", "RestrictedToTenantIds", "RestrictedToProjectGroupIds" }.All(key => grant[key] is JsonArray && grant[key]!.AsArray().Count > 0));
    }

    [Theory]
    [InlineData("deploymentprocess-Projects-1", "DeploymentProcess", OctopusDocumentKind.DeploymentProcess)]
    [InlineData("deploymentprocess-Projects-1-s-4-ABCDE", "DeploymentProcess", OctopusDocumentKind.DeploymentProcessSnapshot)]
    [InlineData("variableset-LibraryVariableSets-1-s-3-FGHIJ", "VariableSet", OctopusDocumentKind.VariableSetSnapshot)]
    [InlineData("feeds-docker-hub", null, OctopusDocumentKind.Feed)]
    [InlineData("teams-spacemanagers-Spaces-1", null, OctopusDocumentKind.Team)]
    public void Classifier_RecognisesRestIds(string id, string documentType, OctopusDocumentKind expected)
    {
        OctopusDocumentClassifier.ClassifyJsonDocument($"{id}.json", id, documentType).Kind.ShouldBe(expected);
    }

    private static JsonObject Resource(string name, string id) => OctopusServer2026Fixtures.Resources(name).Single(r => (string)r["Id"] == id);

    private static IEnumerable<string> IdValues(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == "Links") continue;

                    if (key.EndsWith("Id", StringComparison.Ordinal) && value is JsonValue && value.GetValueKind() == System.Text.Json.JsonValueKind.String) yield return (string)value;

                    if (key.EndsWith("Ids", StringComparison.Ordinal) && value is JsonArray ids)
                        foreach (var id in ids.OfType<JsonValue>()) yield return (string)id;

                    if (value != null)
                        foreach (var nested in IdValues(value)) yield return nested;
                }

                break;
            case JsonArray array:
                foreach (var nested in array.Where(item => item != null).SelectMany(IdValues)) yield return nested;

                break;
        }
    }

    private static IEnumerable<string> SensitiveValuePaths(JsonNode node, string path)
    {
        switch (node)
        {
            case JsonObject obj when obj.ContainsKey("HasValue"):
                yield return path;

                break;
            case JsonObject obj:
                foreach (var (key, value) in obj.Where(p => p.Key != "Links" && p.Value != null))
                foreach (var nested in SensitiveValuePaths(value, $"{path}.{key}")) yield return nested;

                break;
            case JsonArray array:
                foreach (var nested in array.Where(item => item != null).SelectMany(item => SensitiveValuePaths(item, path + "[]"))) yield return nested;

                break;
        }
    }
}

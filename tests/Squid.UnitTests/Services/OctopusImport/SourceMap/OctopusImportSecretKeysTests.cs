using System.Collections.Generic;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;

namespace Squid.UnitTests.Services.OctopusImport.SourceMap;

public class OctopusImportSecretKeysTests
{
    private static readonly Dictionary<(string, OctopusResourceKind), int> Destinations = new()
    {
        [("Environments-1", OctopusResourceKind.Environment)] = 11,
        [("Environments-2", OctopusResourceKind.Environment)] = 12,
        [("Channels-1", OctopusResourceKind.Channel)] = 21
    };

    [Fact]
    public void Prefix_IsPinned_AndAKeyNeverLooksLikeAnExportSuffixedId()
    {
        // Persisted as source_id: renaming the prefix orphans every Missing row a server import recorded.
        OctopusImportSecretKeys.VariablePrefix.ShouldBe("vs:");

        var key = Key("variableset-Projects-1", Secret("Db.Password"));

        key.ShouldStartWith("vs:");
        key.Length.ShouldBeLessThanOrEqualTo(200);
        OctopusImportPriorMappingsNormalizeKeeps(key);
    }

    // The export rewrites variable ids and Squid re-inserts variables on save: the id must never be part of the key.
    [Fact]
    public void SameVariable_FromEitherSource_HasOneKey()
    {
        var fromServer = Secret("Db.Password", id: "c9e2a6a4-61f6-4bb4-9a43-3c4f2b8a1d10", ("Environment", ["Environments-2", "Environments-1"]), ("Role", ["web"]));
        var fromFile = Secret("Db.Password", id: "0f0f0f0f-0000-0000-0000-000000000001", ("Role", ["web"]), ("Environment", ["Environments-1", " Environments-2 "]));

        Key("variableset-Projects-1", fromServer).ShouldBe(Key("variableset-Projects-1-8E3F1C2A9B7D4E6F8A1B2C3D4E5F6A7B", fromFile));
    }

    [Theory]
    [InlineData("other-set", "Db.Password", "Environments-1", "web")]
    [InlineData("variableset-Projects-1", "Db.Pass", "Environments-1", "web")]
    [InlineData("variableset-Projects-1", "Db.Password", "Environments-2", "web")]
    [InlineData("variableset-Projects-1", "Db.Password", "Environments-1", "Web")]
    public void AnyIdentityPartDiffers_KeyDiffers(string owner, string name, string environment, string role)
    {
        var baseline = Key("variableset-Projects-1", Secret("Db.Password", scopes: [("Environment", ["Environments-1"]), ("Role", ["web"])]));

        Key(owner, Secret(name, scopes: [("Environment", [environment]), ("Role", [role])])).ShouldNotBe(baseline);
    }

    [Theory]
    [InlineData("Environment", "Environments-404")]
    [InlineData("Tenant", "Tenants-1")]
    public void ScopeThatCannotBeMapped_HasNoKey(string scopeKey, string value)
    {
        Key("variableset-Projects-1", Secret("Db.Password", scopes: [(scopeKey, [value])])).ShouldBeNull();
    }

    [Fact]
    public void CanonicalScopes_UseSquidDestinationIdsAndRoleText_Sorted()
    {
        var scope = new Dictionary<string, List<string>> { ["Role"] = ["web"], ["Channel"] = ["Channels-1"], ["Environment"] = ["Environments-2", "Environments-1", ""] };

        OctopusImportSecretKeys.CanonicalScopes(scope, "Projects-1", Destination).ShouldBe(["Channel:21", "Environment:11", "Environment:12", "Role:web"]);
    }

    // A deployment recomputes the key from the Squid variable (the source document is gone): both sides must agree byte for byte.
    [Fact]
    public void SquidVariableIdentity_HasTheSourceVariablesKey()
    {
        var source = Secret("Db.Password", scopes: [("Environment", ["Environments-2", "Environments-1"]), ("Role", ["web"])]);
        var imported = Squid.Message.Models.Deployments.Variable.VariableIdentity.Of("Db.Password", Squid.Message.Enums.VariableType.Password, [(Squid.Message.Enums.VariableScopeType.Role, "web"), (Squid.Message.Enums.VariableScopeType.Environment, "12"), (Squid.Message.Enums.VariableScopeType.Environment, "11")]);

        OctopusImportSecretKeys.ForIdentity("VARIABLESET-PROJECTS-1", imported).ShouldBe(Key("variableset-Projects-1", source));
    }

    [Theory]
    [InlineData("Db.Pass", Squid.Message.Enums.VariableType.Password, "11")]
    [InlineData("Db.Password", Squid.Message.Enums.VariableType.String, "11")]
    [InlineData("Db.Password", Squid.Message.Enums.VariableType.Password, "12")]
    public void SquidVariableIdentity_AnyPartDiffers_KeyDiffers(string name, Squid.Message.Enums.VariableType type, string environmentId)
    {
        var identity = Squid.Message.Models.Deployments.Variable.VariableIdentity.Of(name, type, [(Squid.Message.Enums.VariableScopeType.Environment, environmentId)]);

        OctopusImportSecretKeys.ForIdentity("variableset-Projects-1", identity).ShouldNotBe(Key("variableset-Projects-1", Secret("Db.Password", scopes: [("Environment", ["Environments-1"])])));
    }

    private static void OctopusImportPriorMappingsNormalizeKeeps(string key)
        => Squid.Message.Models.OctopusImport.OctopusImportPriorMappings.Normalize(key).ShouldBe(key.ToUpperInvariant());

    private static string Key(string owner, OctopusVariableDto variable) => OctopusImportSecretKeys.ForVariable(owner, "Projects-1", variable, Destination);

    // A stored VariableSecret key hashes the Squid destination id of an Action scope, never the Octopus id: qualifying the action's
    // source id changes only how that destination is found, so keys an earlier import recorded still match (literal pinned).
    [Fact]
    public void ForVariable_ActionScoped_KeyEqualsPreUpgradeKey()
    {
        const string actionId = "6a0b1c2d-0000-4000-8000-000000000002";
        var actions = new Dictionary<(string, OctopusResourceKind), int> { [($"Projects-2/action/{actionId}", OctopusResourceKind.DeploymentAction)] = 31, [($"Projects-1/action/{actionId}", OctopusResourceKind.DeploymentAction)] = 30 };
        int? DestinationOfAction(string sourceId, OctopusResourceKind kind) => actions.TryGetValue((sourceId, kind), out var id) ? id : null;
        var secret = Secret("Db.Password", scopes: ("Action", [actionId]));

        OctopusImportSecretKeys.ForVariable("variableset-Projects-2", "Projects-2", secret, DestinationOfAction).ShouldBe("vs:93673012aa322beac155fbaa4cba5ec3094a2aeee26efefd1d209e5d0dbd5bba");
        OctopusImportSecretKeys.ForVariable("variableset-Projects-2", "Projects-1", secret, DestinationOfAction).ShouldNotBe(OctopusImportSecretKeys.ForVariable("variableset-Projects-2", "Projects-2", secret, DestinationOfAction), "the action of another project is another scope");
    }

    private static int? Destination(string sourceId, OctopusResourceKind kind) => Destinations.TryGetValue((sourceId, kind), out var id) ? id : null;

    private static OctopusVariableDto Secret(string name, string id = "v-1", params (string Key, List<string> Values)[] scopes)
    {
        var variable = new OctopusVariableDto { Id = id, Name = name, Type = "Sensitive", IsSensitive = true };

        foreach (var (key, values) in scopes) variable.Scope[key] = values;

        return variable;
    }
}

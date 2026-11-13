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

        OctopusImportSecretKeys.CanonicalScopes(scope, Destination).ShouldBe(["Channel:21", "Environment:11", "Environment:12", "Role:web"]);
    }

    private static void OctopusImportPriorMappingsNormalizeKeeps(string key)
        => Squid.Message.Models.OctopusImport.OctopusImportPriorMappings.Normalize(key).ShouldBe(key.ToUpperInvariant());

    private static string Key(string owner, OctopusVariableDto variable) => OctopusImportSecretKeys.ForVariable(owner, variable, Destination);

    private static int? Destination(string sourceId, OctopusResourceKind kind) => Destinations.TryGetValue((sourceId, kind), out var id) ? id : null;

    private static OctopusVariableDto Secret(string name, string id = "v-1", params (string Key, List<string> Values)[] scopes)
    {
        var variable = new OctopusVariableDto { Id = id, Name = name, Type = "Sensitive", IsSensitive = true };

        foreach (var (key, values) in scopes) variable.Scope[key] = values;

        return variable;
    }
}

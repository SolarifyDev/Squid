namespace Squid.IntegrationTests.Helpers;

/// <summary>
/// Resources of a whole Octopus space that no project uses, in both source shapes (REST as the API serves them, and
/// export documents): three Squid cannot import (a Maven feed, a legacy Azure management-certificate account, a library
/// scoped to tenant tags), Octopus's built-in package repository, and one feed Squid can import.
/// </summary>
public static class OctopusWholeSpaceResources
{
    public const string SpareFeedId = "Feeds-8";
    public const string SpareFeedName = "Spare registry";

    /// <summary>What the preview skips; the built-in repository never reaches it from the API (the normaliser drops it).</summary>
    public static IReadOnlyList<string> SkippedIds { get; } = ["Feeds-9", "Accounts-9", "LibraryVariableSets-9"];

    public static StubOctopusServer WithUnusedResourcesSquidCannotImport(this StubOctopusServer octopus)
    {
        octopus.Resources["feeds"] = new List<Dictionary<string, object>>
        {
            new() { ["Id"] = SpareFeedId, ["Name"] = SpareFeedName, ["Slug"] = "spare-registry", ["FeedType"] = "Docker", ["FeedUri"] = "https://registry.example.com", ["Password"] = new { HasValue = false } },
            new() { ["Id"] = "Feeds-9", ["Name"] = "Unused Maven", ["Slug"] = "unused-maven", ["FeedType"] = "Maven", ["FeedUri"] = "https://maven.example.com", ["Password"] = new { HasValue = false } },
            new() { ["Id"] = "feeds-builtin", ["Name"] = "Octopus Server (built-in)", ["FeedType"] = "BuiltIn" }
        };
        octopus.Resources["accounts"] = new List<Dictionary<string, object>> { new() { ["Id"] = "Accounts-9", ["Name"] = "Unused Azure", ["AccountType"] = "AzureSubscription", ["SubscriptionNumber"] = "00000000-0000-0000-0000-000000000000", ["CertificateBytes"] = new { HasValue = true }, ["EnvironmentIds"] = Array.Empty<string>() } };
        octopus.Resources["libraryvariablesets"] = new List<Dictionary<string, object>> { new() { ["Id"] = "LibraryVariableSets-9", ["Name"] = "Unused tenant library", ["VariableSetId"] = "variableset-LibraryVariableSets-9", ["ContentType"] = "Variables" } };
        octopus.Resources["variables/variableset-LibraryVariableSets-9"] = new Dictionary<string, object> { ["Id"] = "variableset-LibraryVariableSets-9", ["OwnerId"] = "LibraryVariableSets-9", ["Variables"] = new[] { new { Id = "6f8e2a10-0000-4000-8000-000000000009", Name = "Region", Value = "eu", Type = "String", Scope = new Dictionary<string, string[]> { ["TenantTag"] = ["Regions/EU"] } } } };

        return octopus;
    }

    public static IEnumerable<(string Id, string DocumentType, string Json)> UnusedDocuments() =>
    [
        (SpareFeedId, "Feed", """{"Id":"Feeds-8","Name":"Spare registry","Slug":"spare-registry","FeedType":"Docker","FeedUri":"https://registry.example.com"}"""),
        ("Feeds-9", "Feed", """{"Id":"Feeds-9","Name":"Unused Maven","Slug":"unused-maven","FeedType":"Maven","FeedUri":"https://maven.example.com"}"""),
        ("feeds-builtin", "Feed", """{"Id":"feeds-builtin","Name":"Octopus Server (built-in)","FeedType":"BuiltIn"}"""),
        ("Accounts-9", "Account", """{"Id":"Accounts-9","Name":"Unused Azure","AccountType":"AzureSubscription","EnvironmentIds":[]}"""),
        ("LibraryVariableSets-9", "LibraryVariableSet", """{"Id":"LibraryVariableSets-9","Name":"Unused tenant library","VariableSetId":"variableset-LibraryVariableSets-9","ContentType":"Variables"}"""),
        ("variableset-LibraryVariableSets-9", "LibraryVariables", """{"Id":"variableset-LibraryVariableSets-9","OwnerId":"LibraryVariableSets-9","OwnerType":"LibraryVariableSet","Variables":[{"Id":"6f8e2a10-0000-4000-8000-000000000009","Name":"Region","Value":"eu","Type":"String","Scope":{"TenantTag":["Regions/EU"]}}]}""")
    ];
}

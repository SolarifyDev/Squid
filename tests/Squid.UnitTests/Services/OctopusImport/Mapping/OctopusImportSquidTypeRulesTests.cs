using System.Linq;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Mapping;

/// <summary>
/// The type rules the preview's whole-space scope shares with the confirmation mappers: preview skips or blocks a
/// resource exactly when the mapper would refuse it. The seam theories drive the real mapper with the same input.
/// </summary>
public class OctopusImportSquidTypeRulesTests
{
    [Theory]
    [InlineData("Docker", "Docker Registry")]
    [InlineData("OCI Registry", "Docker Registry")]
    [InlineData("NuGet", "NuGet")]
    [InlineData("Helm", "Helm")]
    [InlineData("GitHub", "GitHub")]
    [InlineData("Maven", null)]
    [InlineData("Npm", null)]
    [InlineData("BuiltIn", null)]
    [InlineData("OctopusProject", null)]
    [InlineData(null, null)]
    public void SquidFeedType_AndTheFeedMapperAgree(string octopusType, string expected)
    {
        var feed = new OctopusFeedDto { Id = "Feeds-1", Name = "Feed", FeedType = octopusType, FeedUri = "https://packages.example.com" };

        var result = new OctopusImportFeedMapper().MapToCreateCommand(Node(feed, OctopusResourceKind.Feed), 7);

        OctopusImportFeedMapper.SquidFeedType(feed).ShouldBe(expected);
        result.Diagnostics.Any(d => d.Code == OctopusImportFeedMappingDiagnosticCodes.UnsupportedFeedType).ShouldBe(expected == null);
    }

    [Theory]
    [InlineData("Token", AccountType.Token)]
    [InlineData("UsernamePassword", AccountType.UsernamePassword)]
    [InlineData("AmazonWebServicesAccount", AccountType.AmazonWebServicesAccount)]
    [InlineData("AzureServicePrincipal", AccountType.AzureServicePrincipal)]
    [InlineData("SshKeyPair", AccountType.SshKeyPair)]
    [InlineData("AzureSubscription", null)]
    [InlineData("None", null)]
    [InlineData("HashiCorpVault", null)]
    [InlineData(null, null)]
    public void SquidAccountType_AndTheAccountMapperAgree(string octopusType, AccountType? expected)
    {
        var account = new OctopusAccountDto { Id = "Accounts-1", Name = "Account", AccountType = octopusType };

        var result = new OctopusImportExternalResourceShellMapper().MapAccountToCreateCommand(Node(account, OctopusResourceKind.Account), new OctopusImportIdMap(), 7);

        OctopusImportExternalResourceShellMapper.SquidAccountType(octopusType).ShouldBe(expected);
        result.Diagnostics.Any(d => d.Code == OctopusImportExternalResourceShellMapper.UnsupportedAccountTypeCode).ShouldBe(expected == null);
    }

    [Fact]
    public void UnsupportedAccountTypeCode_Pinned()
        => OctopusImportExternalResourceShellMapper.UnsupportedAccountTypeCode.ShouldBe("OctopusImport.Account.UnsupportedAccountType");

    [Theory]
    [InlineData("String", false, VariableType.String)]
    [InlineData(null, false, VariableType.String)]
    [InlineData("Sensitive", true, VariableType.Password)]
    [InlineData("Certificate", false, VariableType.Certificate)]
    [InlineData("Boolean", false, VariableType.Boolean)]
    [InlineData("MultiLineText", false, VariableType.MultiLineText)]
    [InlineData("SelectList", false, VariableType.SelectList)]
    [InlineData("AmazonWebServicesAccount", false, null)]
    [InlineData("AzureAccount", false, null)]
    [InlineData("WorkerPool", false, null)]
    public void SquidVariableType(string octopusType, bool isSensitive, VariableType? expected)
        => OctopusImportVariableMapper.SquidVariableType(new OctopusVariableDto { Id = "v", Name = "V", Type = octopusType, IsSensitive = isSensitive }).ShouldBe(expected);

    private static OctopusResourceNode Node(OctopusDocumentDto source, OctopusResourceKind kind)
        => new(source.Id, source.Name, kind, OctopusDocumentKind.Unknown, $"{source.Id}.json", null, null, false, source);
}

using System.Collections.Generic;
using System.Linq;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Secrets;

/// <summary>
/// The words of a refusal over rows a third origin created: the origins that created them are named (server, space and version when
/// an import session recorded them; a file by its fingerprint), at most three, the rest counted.
/// </summary>
public class OctopusImportOriginMessagesTests
{
    private const string Fingerprint = "FEDCBA9876543210FEDCBA9876543210";

    [Fact]
    public void Creators_NamesEachOriginWithWhatItsImportSessionRecorded()
    {
        var origins = new Dictionary<string, OctopusImportInstallationOrigin> { [OctopusImportPriorMappings.Normalize("inst-z")] = new("inst-z", "octopus-z.example.test", "Spaces-3", "2025.2.0") };

        var creators = OctopusImportOriginMessages.Creators([new OctopusImportRowCreator("inst-z", null), new OctopusImportRowCreator("inst-q", null), new OctopusImportRowCreator(null, Fingerprint)], origins);

        creators.ShouldBe(["Octopus installation inst-z (server octopus-z.example.test, space Spaces-3, Octopus 2025.2.0)", "Octopus installation inst-q", $"an export file with fingerprint {Fingerprint}"]);
    }

    [Theory]
    [InlineData(1, "c1")]
    [InlineData(2, "c1 and c2")]
    [InlineData(3, "c1, c2 and c3")]
    [InlineData(5, "c1, c2, c3 and 2 more")]
    public void CreatorsText_AtMostThreeNamedTheRestCounted(int count, string expected)
    {
        OctopusImportOriginMessages.CreatorsText(Enumerable.Range(1, count).Select(i => $"c{i}").ToList()).ShouldBe(expected);
    }

    [Fact]
    public void OtherOriginRows_NamesTheCreatorsBesideBothOrigins()
    {
        var message = OctopusImportOriginMessages.Refused(new OctopusImportFillEvidence(Message.Enums.OctopusImport.OctopusImportFillEvidenceState.Unproven, 0, OctopusImportFillEvidences.OtherOriginRowsBasis), "SOURCE", "RECORDED", ["Octopus installation inst-z"]);

        message.ShouldSatisfyAllConditions(
            m => m.ShouldContain("neither this source (SOURCE) nor the one this space was imported from (RECORDED)"),
            m => m.ShouldContain("Those resources were created by Octopus installation inst-z."),
            m => m.ShouldContain("nothing is linked or written"));
    }

    [Fact]
    public void OtherOriginRows_WithoutCreatorsKeepsTheEarlierWording()
    {
        OctopusImportOriginMessages.OtherOriginRows("SOURCE", "RECORDED").ShouldNotContain("created by");
    }
}

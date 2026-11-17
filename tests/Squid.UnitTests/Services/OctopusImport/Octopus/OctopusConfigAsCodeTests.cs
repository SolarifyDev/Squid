using System.Text.Json.Nodes;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.UnitTests.Services.OctopusImport.Octopus;

/// <summary>Either Octopus signal marks a project Config as Code; the raw-document and DTO overloads always agree.</summary>
public class OctopusConfigAsCodeTests
{
    [Theory]
    [InlineData("VersionControlled", false, true)]
    [InlineData("versioncontrolled", false, true)]
    [InlineData(" VersionControlled ", false, true)]
    [InlineData("Database", false, false)]
    [InlineData(null, true, true)]
    [InlineData("Database", true, true)]
    [InlineData(null, false, false)]
    public void IsVersionControlled_EitherSignal_BothOverloadsAgree(string persistenceType, bool isVersionControlled, bool expected)
    {
        var document = new JsonObject { ["Id"] = "Projects-1", ["IsVersionControlled"] = isVersionControlled };
        if (persistenceType != null) document["PersistenceSettings"] = new JsonObject { ["Type"] = persistenceType };
        var dto = new OctopusProjectDto { Id = "Projects-1", IsVersionControlled = isVersionControlled, PersistenceSettings = persistenceType == null ? null : new OctopusProjectPersistenceSettingsDto { Type = persistenceType } };

        OctopusConfigAsCode.IsVersionControlled(document).ShouldBe(expected);
        OctopusConfigAsCode.IsVersionControlled(dto).ShouldBe(expected);
    }

    // An older server has neither member; a null project is not one.
    [Fact]
    public void OlderServerDocument_OrNull_IsDatabaseBacked()
    {
        OctopusConfigAsCode.IsVersionControlled(new JsonObject { ["Id"] = "Projects-1" }).ShouldBeFalse();
        OctopusConfigAsCode.IsVersionControlled((JsonObject)null).ShouldBeFalse();
        OctopusConfigAsCode.IsVersionControlled((OctopusProjectDto)null).ShouldBeFalse();
    }

    // A malformed member (string flag, settings not an object) never throws and never reads as Config as Code.
    [Fact]
    public void MalformedMembers_ReadAsDatabaseBacked()
    {
        var document = new JsonObject { ["IsVersionControlled"] = "true", ["PersistenceSettings"] = "VersionControlled" };

        OctopusConfigAsCode.IsVersionControlled(document).ShouldBeFalse();
    }

    // The Octopus wire contract (2021.3+ project document).
    [Fact]
    public void WireNames_Pinned()
    {
        OctopusConfigAsCode.VersionControlledPersistenceType.ShouldBe("VersionControlled");
        OctopusConfigAsCode.IsVersionControlledMember.ShouldBe("IsVersionControlled");
        OctopusConfigAsCode.PersistenceSettingsMember.ShouldBe("PersistenceSettings");
        OctopusConfigAsCode.PersistenceTypeMember.ShouldBe("Type");
    }
}

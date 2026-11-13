using System.IO;
using Squid.Core.Persistence.Db;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.SourceMap;

/// <summary>
/// Canonical Octopus source ids: an Octopus export appends one constant 32-hex suffix to every id and embedded reference
/// (<c>Projects-643-&lt;sfx&gt;</c>), while the REST API returns the bare id. Both sources must meet on one key.
/// The suffix below is synthetic; real exports carry a different value.
/// </summary>
public class OctopusImportSourceIdsTests
{
    private const string Suffix = "0123456789ABCDEF0123456789ABCDEF";

    [Theory]
    [InlineData("Projects-643-" + Suffix, "Projects-643")]
    [InlineData("Projects-643-0123456789abcdef0123456789abcdef", "Projects-643")]
    [InlineData("variableset-Projects-1-" + Suffix, "variableset-Projects-1")]
    [InlineData("variableset-Projects-1-s-55-ABCDE-" + Suffix, "variableset-Projects-1-s-55-ABCDE")]
    [InlineData("deploymentprocess-Projects-1-" + Suffix + "/step-0", "deploymentprocess-Projects-1/step-0")]
    [InlineData("deploymentprocess-Projects-1-" + Suffix + "/step-0-" + Suffix, "deploymentprocess-Projects-1/step-0")]
    public void StripExportSuffix_ExportIds_BecomeTheRestId(string exportId, string expected)
        => OctopusImportSourceIds.StripExportSuffix(exportId).ShouldBe(expected);

    [Theory]
    [InlineData("Projects-1")]
    [InlineData("deploymentprocess-Projects-1-s-4-ABCDE")]
    [InlineData("8f2b6c1e-3a4d-4e5f-9a0b-1c2d3e4f5a6b")]
    [InlineData("feeds-builtin-releases")]
    [InlineData("teams-spacemanagers-Spaces-1")]
    [InlineData(Suffix)]                                          // the export-source fingerprint itself has no leading dash
    [InlineData("Projects-1-0123456789ABCDEF0123456789ABCDE")]    // 31 hex
    [InlineData("Projects-1-0123456789ABCDEF0123456789ABCDEF0")]  // 33 hex
    [InlineData("Projects-1-0123456789ABCDEF0123456789ABCDEG")]   // not hex
    [InlineData("")]
    [InlineData(null)]
    public void StripExportSuffix_NonExportIds_AreUnchanged(string id)
        => OctopusImportSourceIds.StripExportSuffix(id).ShouldBe(id);

    [Theory]
    [InlineData("Spaces-1-" + Suffix, Suffix)]
    [InlineData("Spaces-12-0123456789abcdef0123456789abcdef", Suffix)]
    [InlineData("Spaces-1", null)]
    [InlineData("Projects-1-" + Suffix, null)]
    [InlineData("Spaces-1-0123456789ABCDEF0123456789ABCDE", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExportSuffixOf_ReadsTheSuffixOfASuffixedSpaceIdOnly(string spaceId, string expected)
        => OctopusImportSourceIds.ExportSuffixOf(spaceId).ShouldBe(expected);

    [Fact]
    public void ExportSuffixPattern_IsPinned()
    {
        // The data migration applies the same pattern to stored rows; changing it here alone forks the keys.
        OctopusImportSourceIds.ExportSuffixPattern.ShouldBe("-[0-9A-F]{32}(?=$|/)");
    }

    [Fact]
    public void CanonicaliseMigration_UsesTheSamePattern()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, DbUpRunner.ScriptFolder, "20261113_canonicalise_octopus_import_source_ids.sql"));

        sql.ShouldContain($"'{OctopusImportSourceIds.ExportSuffixPattern}'", customMessage: "drift detected: the migration's suffix regex no longer matches OctopusImportSourceIds.ExportSuffixPattern");
    }
}

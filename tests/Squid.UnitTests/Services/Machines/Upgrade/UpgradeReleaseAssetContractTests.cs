using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Producer ⇿ consumer contract for the tentacle self-upgrade download.
///
/// <para>The release workflow (PRODUCER) publishes the upgrade artifact under a
/// specific name; the upgrade strategy (CONSUMER) builds the URL it fetches from
/// the same name. Nothing in the type system links them, so a rename on either
/// side 404s every self-upgrade — exactly the class of bug that shipped when the
/// Windows upgrade URL left <c>$RID</c> unexpanded. These tests pin the coupling
/// so a divergence fails at PR-review time, on every platform, without needing a
/// real agent or a real GitHub release.</para>
///
/// <para>Same pattern as <c>YumUpgradeMethodTests.PackagingRelease_PinnedToWorkflowContract</c>,
/// extended to the zip/tarball GitHub-release artifacts the blue-green upgrade uses.</para>
/// </summary>
public sealed class UpgradeReleaseAssetContractTests
{
    [Fact]
    public void WindowsUpgradeDownloadName_MatchesReleaseWorkflowZipArtifact()
    {
        var workflow = ReadWorkflow("build-publish-windows-tentacle.yml");

        // Producer: the archive name `zip -r -q "../<name>"` publishes, with the
        // version + rid interpolations normalised to {V}/{R}.
        var producer = ExtractArchiveName(workflow, @"squid-tentacle-\$\{IMAGE_TAG\}-\$\{\{\s*matrix\.rid\s*\}\}\.zip");

        // Consumer: the filename the upgrade strategy actually downloads.
        var consumer = FileNameOf(WindowsTentacleUpgradeStrategy.BuildDownloadUrl("{V}", "{R}"));

        producer.ShouldBe(consumer,
            customMessage: $"build-publish-windows-tentacle.yml publishes '{producer}' but " +
                           $"WindowsTentacleUpgradeStrategy.BuildDownloadUrl downloads '{consumer}'. " +
                           "A mismatch 404s every Windows self-upgrade — keep the two in lockstep.");
    }

    [Fact]
    public void LinuxUpgradeDownloadName_MatchesReleaseWorkflowTarballArtifact()
    {
        var workflow = ReadWorkflow("build-publish-linux-tentacle.yml");

        var producer = ExtractArchiveName(workflow, @"squid-tentacle-\$\{IMAGE_TAG\}-\$\{\{\s*matrix\.rid\s*\}\}\.tar\.gz");

        var consumer = FileNameOf(LinuxTentacleUpgradeStrategy.BuildDownloadUrl("{V}", "{R}"));

        producer.ShouldBe(consumer,
            customMessage: $"build-publish-linux-tentacle.yml publishes '{producer}' but " +
                           $"LinuxTentacleUpgradeStrategy.BuildDownloadUrl downloads '{consumer}'. " +
                           "A mismatch 404s every Linux tarball self-upgrade — keep the two in lockstep.");
    }

    private static string ExtractArchiveName(string workflow, string pattern)
    {
        var match = Regex.Match(workflow, pattern);

        match.Success.ShouldBeTrue(
            $"could not find the release archive name (pattern: {pattern}) in the workflow — " +
            "the artifact naming changed. Update this contract AND the upgrade strategy together.");

        return Normalize(match.Value);
    }

    // Reduce a release-artifact name to {V}-version / {R}-rid placeholders so it can be
    // compared structurally to the strategy's BuildDownloadUrl output.
    private static string Normalize(string name) =>
        Regex.Replace(name.Replace("${IMAGE_TAG}", "{V}"), @"\$\{\{\s*matrix\.rid\s*\}\}", "{R}");

    private static string FileNameOf(string url) => url.Split('/').Last();

    private static string ReadWorkflow(string fileName) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", fileName));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate .git — test must run inside the Squid working tree");
    }
}

using System.IO;
using Shouldly;
using Xunit;

namespace Squid.Tentacle.Tests.Certificate;

/// <summary>
/// Rule 12.5 drift detector. Production code MUST construct the certificate manager only via
/// <c>ProductionTentacleCertificateManager.Create</c>, which wires the machine-key encryptor so
/// the cert password (guarding the PFX private key) and the subscription id are encrypted at rest.
/// The encryptor-less <c>new TentacleCertificateManager(certsPath)</c> constructor stays public for
/// tests, so a future production site could silently revert to it and reship plaintext secrets —
/// a regression the unit tests (which build the manager directly) would NOT catch, and which the
/// only end-to-end guard (the Linux <c>D5h</c> register E2E) skips on every non-Linux dev/CI host.
///
/// This cross-platform source scan converts that silent regression into a fast PR-gating failure:
/// the literal <c>new TentacleCertificateManager(</c> may appear in src ONLY inside the factory.
/// </summary>
public sealed class TentacleCertificateManagerConstructionDriftTests
{
    private const string BareConstructor = "new TentacleCertificateManager(";
    private const string FactoryFileName = "ProductionTentacleCertificateManager.cs";

    [Fact]
    public void NoProductionSite_ConstructsCertificateManager_OutsideTheFactory()
    {
        var srcRoot = Path.Combine(RepoRoot(), "src", "Squid.Tentacle");
        Directory.Exists(srcRoot).ShouldBeTrue($"expected Tentacle source tree at {srcRoot}");

        var offenders = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != FactoryFileName)
            .Where(path => File.ReadAllText(path).Contains(BareConstructor, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(srcRoot, path))
            .ToList();

        offenders.ShouldBeEmpty(
            "these production files construct TentacleCertificateManager directly, bypassing at-rest " +
            $"encryption — route them through ProductionTentacleCertificateManager.Create: {string.Join(", ", offenders)}");
    }

    private static string RepoRoot()
    {
        // Walk up from the test binary dir until we find .git — stable from any cwd.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate .git — test must run inside the Squid repo working tree");
    }
}

using System.IO.Compression;
using Shouldly;
using Squid.Calamari.Commands;
using Squid.Calamari.Commands.Common;
using Squid.Calamari.Commands.Package;
using Squid.Calamari.Tests.Calamari.Commands.Common;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.Package;

/// <summary>
/// G1.4 — pipeline-level tests for <see cref="ExtractPackageStep"/>.
///
/// <para>Wire literal: <c>Squid.Action.Package.OriginalPath</c> points at a
/// .nupkg / .zip file on disk. Step extracts into <c>context.WorkingDirectory</c>
/// and is a no-op when the literal is unset (standalone-script case).</para>
/// </summary>
[Collection(RewriterEnvVarSerialCollection.Name)]
public sealed class ExtractPackageStepTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _archiveDir;

    public ExtractPackageStepTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"extract-step-{Guid.NewGuid():N}");
        _archiveDir = Path.Combine(Path.GetTempPath(), $"extract-arch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        Directory.CreateDirectory(_archiveDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
        if (Directory.Exists(_archiveDir)) Directory.Delete(_archiveDir, recursive: true);
    }

    // ── Enable-gating ───────────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_PackagePathSet_RunsStep()
    {
        var context = BuildContext(packagePath: "/some/path.nupkg");
        new ExtractPackageStep().IsEnabled(context).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsEnabled_PackagePathBlankOrUnset_SkipsStep(string? path)
    {
        // Standalone-script deploys (no package) hit this path. MUST be a
        // no-op — otherwise every script-only deploy throws on the missing
        // archive.
        var context = BuildContext(packagePath: path);
        new ExtractPackageStep().IsEnabled(context).ShouldBeFalse();
    }

    // ── Happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_NupkgArchive_ExtractsIntoWorkingDir()
    {
        var nupkg = Path.Combine(_archiveDir, "HelloApp.1.0.0.nupkg");
        using (var zip = ZipFile.Open(nupkg, ZipArchiveMode.Create))
        {
            AddEntry(zip, "Web.config", "<configuration />");
            AddEntry(zip, "appsettings.json", """{"k":"v"}""");
            AddEntry(zip, "bin/app.dll", "fake bytes");
        }

        var context = BuildContext(packagePath: nupkg);

        await new ExtractPackageStep().ExecuteAsync(context, CancellationToken.None);

        File.Exists(Path.Combine(_workDir, "Web.config")).ShouldBeTrue();
        File.Exists(Path.Combine(_workDir, "appsettings.json")).ShouldBeTrue();
        File.Exists(Path.Combine(_workDir, "bin", "app.dll")).ShouldBeTrue();
    }

    [Fact]
    public async Task Execute_ZipArchive_AlsoSupported()
    {
        // .zip is accepted alongside .nupkg — same engine, just a different
        // extension. Operators bundling configs as .zip MUST also work.
        var zip = Path.Combine(_archiveDir, "configs.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            AddEntry(z, "config.json", """{"k":"v"}""");

        var context = BuildContext(packagePath: zip);

        await new ExtractPackageStep().ExecuteAsync(context, CancellationToken.None);

        File.Exists(Path.Combine(_workDir, "config.json")).ShouldBeTrue();
    }

    // ── Unsupported extension ───────────────────────────────────────────────

    [Fact]
    public async Task Execute_UnsupportedExtension_ThrowsWithGuidance()
    {
        // PR-2: .tar.gz / .tar are NOW supported. Still-unsupported formats
        // (.rar, arbitrary text) MUST throw with the operator-facing
        // supported-formats list naming the wire literal to unset.
        var bad = Path.Combine(_archiveDir, "package.rar");
        File.WriteAllText(bad, "fake rar");

        var context = BuildContext(packagePath: bad);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            new ExtractPackageStep().ExecuteAsync(context, CancellationToken.None));

        ex.Message.ShouldContain("unsupported extension");
        ex.Message.ShouldContain(PackageVariableNames.OriginalPath);
        // Operator-facing list MUST include each supported format so the
        // operator can repack without guessing.
        foreach (var ext in PackageExtractorRegistry.SupportedExtensions)
            ex.Message.ShouldContain(ext);
    }

    // ── PR-2 — multi-format dispatch ────────────────────────────────────────

    [Fact]
    public async Task Execute_TarArchive_ExtractsIntoWorkingDir()
    {
        var tar = Path.Combine(_archiveDir, "build-output.tar");
        using (var fs = File.Create(tar))
        using (var w = new System.Formats.Tar.TarWriter(fs, leaveOpen: true))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("body");
            var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "app/run.sh")
            {
                DataStream = new MemoryStream(bytes)
            };
            w.WriteEntry(entry);
        }

        var context = BuildContext(packagePath: tar);
        await new ExtractPackageStep().ExecuteAsync(context, CancellationToken.None);

        File.Exists(Path.Combine(_workDir, "app", "run.sh")).ShouldBeTrue();
        File.ReadAllText(Path.Combine(_workDir, "app", "run.sh")).ShouldBe("body");
    }

    [Fact]
    public async Task Execute_TarGzArchive_ExtractsIntoWorkingDir()
    {
        var targz = Path.Combine(_archiveDir, "build-output.tar.gz");
        using (var fs = File.Create(targz))
        using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal))
        using (var w = new System.Formats.Tar.TarWriter(gz, leaveOpen: true))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("compressed");
            w.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "config.yaml")
            {
                DataStream = new MemoryStream(bytes)
            });
        }

        var context = BuildContext(packagePath: targz);
        await new ExtractPackageStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "config.yaml")).ShouldBe("compressed");
    }

    [Fact]
    public async Task Execute_SevenZipArchive_ExtractsIntoWorkingDir()
    {
        // PR-11: .7z is now extracted via SharpCompress (previously a deferral
        // failure). Real py7zr-generated 7z bytes drive the production extractor
        // end-to-end through the pipeline step.
        var sevenZ = SevenZipTestFixtures.WriteToFile(
            SevenZipTestFixtures.Happy, Path.Combine(_archiveDir, "pkg.7z"));

        var context = BuildContext(packagePath: sevenZ);
        await new ExtractPackageStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "readme.txt")).ShouldBe("hello from 7z");
        File.ReadAllText(Path.Combine(_workDir, "bin", "app.dll")).ShouldBe("fake-binary-content");
    }

    // ── Failure surface ─────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_MalformedArchive_ThrowsWithReason()
    {
        var bad = Path.Combine(_archiveDir, "broken.nupkg");
        File.WriteAllText(bad, "not really a zip");

        var context = BuildContext(packagePath: bad);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            new ExtractPackageStep().ExecuteAsync(context, CancellationToken.None));

        ex.Message.ShouldContain("failed to extract");
    }

    [Fact]
    public async Task Execute_ZipSlipEntry_ThrowsHaltsDeploy()
    {
        // The whole rewriter pipeline assumes a fully-extracted package.
        // A zip-slip mid-extract = abort the whole deploy, not a warn-and-continue.
        var archive = Path.Combine(_archiveDir, "evil.nupkg");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            AddEntry(zip, "ok.txt", "fine");
            AddEntry(zip, "../../escape.txt", "should-not-write");
        }

        var context = BuildContext(packagePath: archive);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            new ExtractPackageStep().ExecuteAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task Execute_WorkingDirNotSet_Throws()
    {
        var archive = Path.Combine(_archiveDir, "ok.nupkg");
        using (var z = ZipFile.Open(archive, ZipArchiveMode.Create))
            AddEntry(z, "x.txt", "x");

        var context = BuildContext(packagePath: archive);
        context.WorkingDirectory = null;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            new ExtractPackageStep().ExecuteAsync(context, CancellationToken.None));
    }

    // ── Wire-contract pinning ───────────────────────────────────────────────

    [Fact]
    public void OriginalPathVariableName_PinnedHandlerAgnostic()
    {
        // Rule 8 drift detector — operators + future server-side handlers
        // emit this exact literal. Silent rename would mean "the package
        // never gets extracted, but every test still passes locally".
        PackageVariableNames.OriginalPath
            .ShouldBe("Squid.Action.Package.OriginalPath");
    }

    // ── Idempotency ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_ReExtract_OverwritesAndStillSucceeds()
    {
        // Re-deploy scenario: same archive extracts to a dir that already
        // has the files. ZipExtractor.Extract(... overwrite:true) handles
        // this; step MUST NOT skip-on-exists.
        var archive = Path.Combine(_archiveDir, "v2.nupkg");
        using (var z = ZipFile.Open(archive, ZipArchiveMode.Create))
            AddEntry(z, "version.txt", "v2");

        File.WriteAllText(Path.Combine(_workDir, "version.txt"), "v1");

        var context = BuildContext(packagePath: archive);
        await new ExtractPackageStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "version.txt")).ShouldBe("v2");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private RunScriptCommandContext BuildContext(string? packagePath)
    {
        var vars = new VariableSet();
        if (packagePath != null) vars.Set(PackageVariableNames.OriginalPath, packagePath);

        return new RunScriptCommandContext
        {
            ScriptPath = Path.Combine(_workDir, "script.sh"),
            VariablesPath = Path.Combine(_workDir, "variables.json"),
            WorkingDirectory = _workDir,
            Variables = vars
        };
    }

    private static void AddEntry(ZipArchive zip, string name, string contents)
    {
        var entry = zip.CreateEntry(name);
        using var s = entry.Open();
        using var w = new StreamWriter(s);
        w.Write(contents);
    }
}

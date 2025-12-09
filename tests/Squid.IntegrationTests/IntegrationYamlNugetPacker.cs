using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Serilog;
using Squid.Core.Services.Common;

namespace Squid.IntegrationTests;

[Collection("Sequential")]
public class IntegrationYamlNugetPacker : IntegrationTestBase, IClassFixture<IntegrationFixture<IntegrationYamlNugetPacker>>
{
    public IntegrationYamlNugetPacker(IntegrationFixture<IntegrationYamlNugetPacker> fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task ShouldCreateNugetPackageWithYamlFiles()
    {
        await Run<IYamlNuGetPacker>(packer =>
        {
            var yamlFiles = new Dictionary<string, byte[]>
            {
                { "deployment.yaml", "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: test-config"u8.ToArray() }
            };

            var version = "1.0.0-test";

            var packageId = "Squid.Test.Package";

            var packageBytes = packer.CreateNuGetPackageFromYamlBytes(yamlFiles, version, packageId);

            packageBytes.ShouldNotBeNull();

            packageBytes.Length.ShouldBeGreaterThan(0);

            using var stream = new MemoryStream(packageBytes);

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var nuspecEntry = archive.GetEntry("Squid.nuspec");

            nuspecEntry.ShouldNotBeNull();

            using var nuspecStream = nuspecEntry.Open();

            using var reader = new StreamReader(nuspecStream);

            var nuspecContent = reader.ReadToEnd();

            nuspecContent.ShouldContain(version);

            nuspecContent.ShouldContain(packageId);

            foreach (var yamlFile in yamlFiles.Keys)
            {
                var entry = archive.GetEntry($"content/{yamlFile}");

                entry.ShouldNotBeNull();
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task ShouldCreateNugetPackageWithYamlStreams()
    {
        await Run<IYamlNuGetPacker>(packer =>
        {
            using var yamlStream = new MemoryStream("apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: test-config"u8.ToArray());

            var yamlFiles = new Dictionary<string, Stream>
            {
                { "deployment.yaml", yamlStream }
            };

            var version = "1.0.0-stream";

            var packageId = "Squid.Test.Stream.Package";

            var packageBytes = packer.CreateNuGetPackageFromYamlStreams(yamlFiles, version, packageId);

            packageBytes.ShouldNotBeNull();

            packageBytes.Length.ShouldBeGreaterThan(0);

            using var stream = new MemoryStream(packageBytes);

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var nuspecEntry = archive.GetEntry("Squid.nuspec");

            nuspecEntry.ShouldNotBeNull();

            using var nuspecStream = nuspecEntry.Open();

            using var reader = new StreamReader(nuspecStream);

            var nuspecContent = reader.ReadToEnd();

            nuspecContent.ShouldContain(version);

            nuspecContent.ShouldContain(packageId);

            foreach (var yamlFile in yamlFiles.Keys)
            {
                var entry = archive.GetEntry($"content/{yamlFile}");

                entry.ShouldNotBeNull();
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }
}
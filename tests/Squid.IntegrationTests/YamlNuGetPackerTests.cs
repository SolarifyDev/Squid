using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Shouldly;
using Squid.Core.Services.Common;
using Squid.IntegrationTests.Fixtures;
using Xunit;

namespace Squid.IntegrationTests;

[Collection("Sequential")]
public class YamlNuGetPackerTests : TestBase<YamlNuGetPackerTests>
{
    [Fact]
    public void CreateNuGetPackageFromYamlBytes_ShouldContainNuspecAndYamlFiles()
    {
        var yamlFiles = new Dictionary<string, byte[]>
        {
            { "deployment.yaml", "apiVersion: v1\nkind: ConfigMap"u8.ToArray() }
        };

        var packer = Resolve<IYamlNuGetPacker>();
        var packageBytes = packer.CreateNuGetPackageFromYamlBytes(yamlFiles, "1.0.0", "Squid.Test");

        packageBytes.ShouldNotBeNull();
        packageBytes.Length.ShouldBeGreaterThan(0);
        
        using var stream = new MemoryStream(packageBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        archive.GetEntry("Squid.nuspec").ShouldNotBeNull();
        archive.GetEntry("content/deployment.yaml").ShouldNotBeNull();
    }

    [Fact]
    public void CreateNuGetPackageFromYamlBytes_ShouldContainMultipleYamlFiles()
    {
        var yamlFiles = new Dictionary<string, byte[]>
        {
            { "deployment.yaml", "apiVersion: v1\nkind: Deployment"u8.ToArray() },
            { "service.yaml", "apiVersion: v1\nkind: Service"u8.ToArray() },
            { "configmap.yaml", "apiVersion: v1\nkind: ConfigMap"u8.ToArray() }
        };

        var packer = Resolve<IYamlNuGetPacker>();
        var packageBytes = packer.CreateNuGetPackageFromYamlBytes(yamlFiles, "2.0.0", "Squid.MultiFile");

        packageBytes.ShouldNotBeNull();
        packageBytes.Length.ShouldBeGreaterThan(0);
        
        using var stream = new MemoryStream(packageBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        archive.GetEntry("Squid.nuspec").ShouldNotBeNull();
        archive.GetEntry("content/deployment.yaml").ShouldNotBeNull();
        archive.GetEntry("content/service.yaml").ShouldNotBeNull();
        archive.GetEntry("content/configmap.yaml").ShouldNotBeNull();
    }

    [Fact]
    public void CreateNuGetPackageFromYamlStreams_ShouldContainNuspecAndYamlFiles()
    {
        using var yamlStream = new MemoryStream("apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: test-config"u8.ToArray());

        var yamlFiles = new Dictionary<string, Stream>
        {
            { "deployment.yaml", yamlStream }
        };

        var packer = Resolve<IYamlNuGetPacker>();
        var packageBytes = packer.CreateNuGetPackageFromYamlStreams(yamlFiles, "1.0.0-stream", "Squid.Test.Stream");

        packageBytes.ShouldNotBeNull();
        packageBytes.Length.ShouldBeGreaterThan(0);
        
        using var stream = new MemoryStream(packageBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        archive.GetEntry("Squid.nuspec").ShouldNotBeNull();
        archive.GetEntry("content/deployment.yaml").ShouldNotBeNull();

        using var nuspecStream = archive.GetEntry("Squid.nuspec")!.Open();
        using var reader = new StreamReader(nuspecStream);
        var nuspecContent = reader.ReadToEnd();
        
        nuspecContent.ShouldContain("1.0.0-stream");
        nuspecContent.ShouldContain("Squid.Test.Stream");
    }

    [Fact]
    public void CreateNuGetPackageFromYamlBytes_ShouldGenerateValidNuspec()
    {
        var yamlFiles = new Dictionary<string, byte[]>
        {
            { "manifest.yaml", "apiVersion: v1\nkind: List"u8.ToArray() }
        };

        var packer = Resolve<IYamlNuGetPacker>();
        var packageBytes = packer.CreateNuGetPackageFromYamlBytes(yamlFiles, "3.1.0", "Squid.Manifest");

        packageBytes.ShouldNotBeNull();
        packageBytes.Length.ShouldBeGreaterThan(0);
        
        using var stream = new MemoryStream(packageBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var nuspecEntry = archive.GetEntry("Squid.nuspec");
        nuspecEntry.ShouldNotBeNull();

        using var nuspecStream = nuspecEntry!.Open();
        using var reader = new StreamReader(nuspecStream);
        var nuspecContent = reader.ReadToEnd();
        
        nuspecContent.ShouldContain("<id>Squid.Manifest</id>");
        nuspecContent.ShouldContain("<version>3.1.0</version>");
        nuspecContent.ShouldContain("<description>");
    }

    [Fact]
    public void CreateNuGetPackageFromEmptyYamlFiles_ShouldStillCreatePackage()
    {
        var yamlFiles = new Dictionary<string, byte[]>();

        var packer = Resolve<IYamlNuGetPacker>();
        var packageBytes = packer.CreateNuGetPackageFromYamlBytes(yamlFiles, "1.0.0-empty", "Squid.Empty");

        packageBytes.ShouldNotBeNull();
        packageBytes.Length.ShouldBeGreaterThan(0);
        
        using var stream = new MemoryStream(packageBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        archive.GetEntry("Squid.nuspec").ShouldNotBeNull();
    }
}

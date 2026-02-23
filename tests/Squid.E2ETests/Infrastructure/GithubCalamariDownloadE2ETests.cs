using System.IO.Compression;
using Microsoft.Extensions.Configuration;
using Squid.Core.Services.Common;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Infrastructure;

[Trait("Category", "E2E")]
public class GithubCalamariDownloadE2ETests
{
    [Fact]
    public async Task DownloadPackageAsync_RealGithubPackagesDownload_ReturnsValidNuGetPackage()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        var username = Environment.GetEnvironmentVariable("SQUID_E2E_GITHUB_PKG_USERNAME")
            ?? config["GithubPackageDownloadE2E:Username"];
        var token = Environment.GetEnvironmentVariable("SQUID_E2E_GITHUB_PKG_TOKEN")
            ?? config["GithubPackageDownloadE2E:Token"];
        var version = Environment.GetEnvironmentVariable("SQUID_E2E_GITHUB_PKG_CALAMARI_VERSION")
            ?? config["GithubPackageDownloadE2E:CalamariVersion"];
        var packageId = Environment.GetEnvironmentVariable("SQUID_E2E_GITHUB_PKG_ID")
            ?? config["GithubPackageDownloadE2E:PackageId"];

        if (string.IsNullOrWhiteSpace(packageId))
            packageId = "Calamari";

        if (!EnsureConfigured(username, token, version, packageId))
            return;

        var downloader = new GithubPackageDownloader(username, token);

        using var stream = await downloader.DownloadPackageAsync(packageId, version);

        stream.Length.ShouldBeGreaterThan(0);
        stream.Position = 0;

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        archive.Entries.Count.ShouldBeGreaterThan(0);
        archive.Entries.ShouldContain(e =>
            e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
    }

    private static bool EnsureConfigured(string username, string token, string version, string packageId)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(username))
            missing.Add("SQUID_E2E_GITHUB_PKG_USERNAME");

        if (string.IsNullOrWhiteSpace(token))
            missing.Add("SQUID_E2E_GITHUB_PKG_TOKEN");

        if (string.IsNullOrWhiteSpace(version))
            missing.Add("SQUID_E2E_GITHUB_PKG_CALAMARI_VERSION");

        if (missing.Count == 0)
            return true;

        Console.WriteLine(
            $"Real GitHub package download E2E test skipped. Missing env vars: {string.Join(", ", missing)}. " +
            $"Set package id via SQUID_E2E_GITHUB_PKG_ID (default: {packageId}).");
        return false;
    }
}

using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.Http;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class HttpPackageContentFetcherTests
{
    private static ExternalFeed CreateFeed(string feedType = "Generic", string feedUri = "https://packages.example.com", string username = null, string password = null)
    {
        return new ExternalFeed
        {
            Id = 1,
            FeedType = feedType,
            FeedUri = feedUri,
            Username = username,
            Password = password
        };
    }

    private static byte[] CreateTarGzArchive(Dictionary<string, string> files, string rootDir = "package")
    {
        using var ms = new MemoryStream();
        using (var gzipStream = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tarWriter = new TarWriter(gzipStream))
        {
            foreach (var kvp in files)
            {
                var entryPath = string.IsNullOrEmpty(rootDir) ? kvp.Key : $"{rootDir}/{kvp.Key}";
                var dataBytes = Encoding.UTF8.GetBytes(kvp.Value);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, entryPath)
                {
                    DataStream = new MemoryStream(dataBytes)
                };
                tarWriter.WriteEntry(entry);
            }
        }

        return ms.ToArray();
    }

    private static byte[] CreateZipArchive(Dictionary<string, string> files, string rootDir = "package")
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var kvp in files)
            {
                var entryPath = string.IsNullOrEmpty(rootDir) ? kvp.Key : $"{rootDir}/{kvp.Key}";
                var entry = archive.CreateEntry(entryPath);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(kvp.Value);
            }
        }

        return ms.ToArray();
    }

    // === ExtractArchive Tests ===

    [Fact]
    public void ExtractArchive_ValidTarGz_ExtractsYamlFiles()
    {
        var archive = CreateTarGzArchive(new Dictionary<string, string>
        {
            ["deployment.yaml"] = "apiVersion: v1\nkind: Deployment",
            ["service.yml"] = "apiVersion: v1\nkind: Service"
        });

        var files = HttpPackageContentFetcher.ExtractArchive(archive);

        files.ShouldContainKey("deployment.yaml");
        files.ShouldContainKey("service.yml");
        files.Count.ShouldBe(2);
    }

    [Fact]
    public void ExtractArchive_ValidZip_ExtractsYamlFiles()
    {
        var archive = CreateZipArchive(new Dictionary<string, string>
        {
            ["deployment.yaml"] = "apiVersion: v1\nkind: Deployment",
            ["config.json"] = "{ \"key\": \"value\" }"
        });

        var files = HttpPackageContentFetcher.ExtractArchive(archive);

        files.ShouldContainKey("deployment.yaml");
        files.ShouldContainKey("config.json");
        files.Count.ShouldBe(2);
    }

    [Fact]
    public void ExtractArchive_FilterNonYaml_OnlyYamlReturned()
    {
        var archive = CreateTarGzArchive(new Dictionary<string, string>
        {
            ["deployment.yaml"] = "apiVersion: v1",
            ["README.md"] = "# Package readme",
            ["install.sh"] = "#!/bin/bash"
        });

        var files = HttpPackageContentFetcher.ExtractArchive(archive);

        files.ShouldContainKey("deployment.yaml");
        files.ShouldNotContainKey("README.md");
        files.ShouldNotContainKey("install.sh");
        files.Count.ShouldBe(1);
    }

    [Fact]
    public void ExtractArchive_InvalidArchive_Throws()
    {
        var badBytes = Encoding.UTF8.GetBytes("not an archive");

        Should.Throw<InvalidOperationException>(() => HttpPackageContentFetcher.ExtractArchive(badBytes))
            .Message.ShouldContain("Unsupported archive format");
    }

    [Fact]
    public void ExtractArchive_NestedPaths_PreservesRelative()
    {
        var archive = CreateTarGzArchive(new Dictionary<string, string>
        {
            ["templates/deployment.yaml"] = "apiVersion: v1",
            ["templates/service.yaml"] = "apiVersion: v1"
        });

        var files = HttpPackageContentFetcher.ExtractArchive(archive);

        // Root dir "package" is stripped, but nested "templates" remains
        files.ShouldContainKey("templates/deployment.yaml");
        files.ShouldContainKey("templates/service.yaml");
    }

    // === BuildDownloadUrl Tests ===

    [Fact]
    public void BuildDownloadUrl_GitHubFeed_GitHubPattern()
    {
        var feed = CreateFeed("GitHub", "https://api.github.com");

        var url = HttpPackageContentFetcher.BuildDownloadUrl(feed, "org/repo", "v1.0.0");

        url.ShouldBe("https://api.github.com/repos/org/repo/tarball/v1.0.0");
    }

    [Fact]
    public void BuildDownloadUrl_GenericFeed_DefaultPattern()
    {
        var feed = CreateFeed("Generic", "https://packages.example.com");

        var url = HttpPackageContentFetcher.BuildDownloadUrl(feed, "my-package", "2.0.0");

        url.ShouldBe("https://packages.example.com/my-package/2.0.0");
    }

    [Fact]
    public void BuildDownloadUrl_EmptyVersion_OmitsVersionInUrl()
    {
        var feed = CreateFeed("Generic", "https://packages.example.com");

        var url = HttpPackageContentFetcher.BuildDownloadUrl(feed, "my-package", "");

        url.ShouldBe("https://packages.example.com/my-package");
    }

    [Fact]
    public void BuildDownloadUrl_NuGetFeed_NuGetPattern()
    {
        var feed = CreateFeed("NuGet", "https://nuget.example.com");

        var url = HttpPackageContentFetcher.BuildDownloadUrl(feed, "MyPackage", "1.0.0");

        url.ShouldBe("https://nuget.example.com/api/v2/package/MyPackage/1.0.0");
    }

    // === BuildAuthHeaders Tests ===

    [Fact]
    public void BuildAuthHeaders_WithCredentials_SetsBasicAuth()
    {
        var feed = CreateFeed(username: "user", password: "pass");

        var headers = HttpPackageContentFetcher.BuildAuthHeaders(feed);

        headers.ShouldContainKey("Authorization");
        headers["Authorization"].ShouldStartWith("Basic ");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(headers["Authorization"].Replace("Basic ", "")));
        decoded.ShouldBe("user:pass");
    }

    [Fact]
    public void BuildAuthHeaders_PasswordOnly_SetsBearerToken()
    {
        var feed = CreateFeed(password: "my-token");

        var headers = HttpPackageContentFetcher.BuildAuthHeaders(feed);

        headers.ShouldContainKey("Authorization");
        headers["Authorization"].ShouldBe("Bearer my-token");
    }

    [Fact]
    public void BuildAuthHeaders_PublicFeed_NoAuthHeader()
    {
        var feed = CreateFeed();

        var headers = HttpPackageContentFetcher.BuildAuthHeaders(feed);

        headers.ShouldNotContainKey("Authorization");
    }

    [Fact]
    public void BuildAuthHeaders_GitHubFeed_SetsAcceptHeader()
    {
        var feed = CreateFeed(feedType: "GitHub");

        var headers = HttpPackageContentFetcher.BuildAuthHeaders(feed);

        headers["Accept"].ShouldBe("application/vnd.github+json");
        headers["X-GitHub-Api-Version"].ShouldBe("2022-11-28");
    }

    [Fact]
    public void BuildAuthHeaders_GitHubTokenOnly_UsesTokenPrefix()
    {
        var feed = CreateFeed(feedType: "GitHub Repository Feed", password: "ghp_abc123");

        var headers = HttpPackageContentFetcher.BuildAuthHeaders(feed);

        headers["Authorization"].ShouldBe("token ghp_abc123");
    }

    [Fact]
    public void BuildAuthHeaders_GitHubWithUsernamePassword_UsesBasicAuth()
    {
        var feed = CreateFeed(feedType: "GitHub", username: "user", password: "pass");

        var headers = HttpPackageContentFetcher.BuildAuthHeaders(feed);

        headers["Authorization"].ShouldStartWith("Basic ");
    }

    // === ParseHelmIndexForChartUrl Tests ===

    [Fact]
    public void ParseHelmIndexForChartUrl_AbsoluteUrl_ReturnsUrl()
    {
        var indexYaml = @"
entries:
  openclaw:
  - version: 1.5.7
    urls:
    - https://serhanekicii.github.io/openclaw-helm/openclaw-1.5.7.tgz
";

        var url = HttpPackageContentFetcher.ParseHelmIndexForChartUrl(indexYaml, "openclaw", "1.5.7", "https://serhanekicii.github.io/openclaw-helm");

        url.ShouldBe("https://serhanekicii.github.io/openclaw-helm/openclaw-1.5.7.tgz");
    }

    [Fact]
    public void ParseHelmIndexForChartUrl_RelativeUrl_PrefixesBaseUri()
    {
        var indexYaml = @"
entries:
  my-chart:
  - version: 2.0.0
    urls:
    - my-chart-2.0.0.tgz
";

        var url = HttpPackageContentFetcher.ParseHelmIndexForChartUrl(indexYaml, "my-chart", "2.0.0", "https://charts.example.com");

        url.ShouldBe("https://charts.example.com/my-chart-2.0.0.tgz");
    }

    [Fact]
    public void ParseHelmIndexForChartUrl_VersionNotFound_ReturnsNull()
    {
        var indexYaml = @"
entries:
  my-chart:
  - version: 1.0.0
    urls:
    - my-chart-1.0.0.tgz
";

        var url = HttpPackageContentFetcher.ParseHelmIndexForChartUrl(indexYaml, "my-chart", "2.0.0", "https://charts.example.com");

        url.ShouldBeNull();
    }

    [Fact]
    public void ParseHelmIndexForChartUrl_ChartNotFound_ReturnsNull()
    {
        var indexYaml = @"
entries:
  other-chart:
  - version: 1.0.0
    urls:
    - other-chart-1.0.0.tgz
";

        var url = HttpPackageContentFetcher.ParseHelmIndexForChartUrl(indexYaml, "my-chart", "1.0.0", "https://charts.example.com");

        url.ShouldBeNull();
    }

    [Fact]
    public void ParseHelmIndexForChartUrl_MultipleVersions_CorrectOne()
    {
        var indexYaml = @"
entries:
  my-chart:
  - version: 2.0.0
    urls:
    - https://example.com/my-chart-2.0.0.tgz
  - version: 1.0.0
    urls:
    - https://example.com/my-chart-1.0.0.tgz
";

        var url = HttpPackageContentFetcher.ParseHelmIndexForChartUrl(indexYaml, "my-chart", "1.0.0", "https://example.com");

        url.ShouldBe("https://example.com/my-chart-1.0.0.tgz");
    }

    [Fact]
    public void ParseHelmIndexForChartUrl_QuotedChartName_Matches()
    {
        var indexYaml = @"
entries:
  ""my-chart"":
  - version: 1.0.0
    urls:
    - my-chart-1.0.0.tgz
";

        var url = HttpPackageContentFetcher.ParseHelmIndexForChartUrl(indexYaml, "my-chart", "1.0.0", "https://charts.example.com");

        url.ShouldBe("https://charts.example.com/my-chart-1.0.0.tgz");
    }

    [Fact]
    public void ParseHelmIndexForChartUrl_StandaloneVersionField_Matches()
    {
        var indexYaml = @"
entries:
  my-chart:
  - appVersion: 1.0.0
    version: 1.0.0
    urls:
    - my-chart-1.0.0.tgz
";

        var url = HttpPackageContentFetcher.ParseHelmIndexForChartUrl(indexYaml, "my-chart", "1.0.0", "https://charts.example.com");

        url.ShouldBe("https://charts.example.com/my-chart-1.0.0.tgz");
    }

    [Fact]
    public void ParseHelmIndexForChartUrl_UrlsBeforeVersion_Matches()
    {
        // Real-world Helm index format: urls appears before version, many fields in between
        var indexYaml = @"apiVersion: v1
entries:
  openclaw:
  - annotations:
      artifacthub.io/category: ai-machine-learning
    apiVersion: v2
    appVersion: 2026.3.24
    created: ""2026-03-26T07:20:46.25548771Z""
    dependencies:
    - name: app-template
      repository: https://bjw-s-labs.github.io/helm-charts/
      version: 4.6.2
    description: Helm chart for deploying OpenClaw
    digest: bc9fbc020110eb700b2aefbea82f47066393a37f
    name: openclaw
    urls:
    - https://github.com/serhanekicii/openclaw-helm/releases/download/openclaw-1.5.7/openclaw-1.5.7.tgz
    version: 1.5.7
  - annotations:
      artifacthub.io/category: ai-machine-learning
    urls:
    - https://github.com/serhanekicii/openclaw-helm/releases/download/openclaw-1.5.6/openclaw-1.5.6.tgz
    version: 1.5.6
generated: ""2026-03-26T07:20:46.255488292Z""
";

        var url = HttpPackageContentFetcher.ParseHelmIndexForChartUrl(indexYaml, "openclaw", "1.5.7", "https://serhanekicii.github.io/openclaw-helm");

        url.ShouldBe("https://github.com/serhanekicii/openclaw-helm/releases/download/openclaw-1.5.7/openclaw-1.5.7.tgz");
    }

    [Fact]
    public void ParseHelmIndexForChartUrl_UrlsBeforeVersion_SecondEntry()
    {
        var indexYaml = @"entries:
  openclaw:
  - urls:
    - https://example.com/openclaw-2.0.0.tgz
    version: 2.0.0
  - urls:
    - https://example.com/openclaw-1.5.7.tgz
    version: 1.5.7
";

        var url = HttpPackageContentFetcher.ParseHelmIndexForChartUrl(indexYaml, "openclaw", "1.5.7", "https://example.com");

        url.ShouldBe("https://example.com/openclaw-1.5.7.tgz");
    }
}

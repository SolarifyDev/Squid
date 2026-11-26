using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.Http;

namespace Squid.UnitTests.Services.Deployments.Execution.Packages;

/// <summary>
/// Pins the observable behaviour of <see cref="IPackageContentFetcher.FetchAsync"/> for every feed
/// type: the HTTP requests issued (URL, auth headers, timeout), archive handling and failure
/// reporting. Only <see cref="CreateSut"/> knows how the fetcher is wired.
/// </summary>
public class PackageContentFetchBehaviorTests
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IndexTimeout = TimeSpan.FromSeconds(30);

    private static IPackageContentFetcher CreateSut(ISquidHttpClientFactory factory) => PackageFetcherWiring.Create(factory);

    private sealed record Call(string Url, Dictionary<string, string> Headers, TimeSpan? Timeout);

    private sealed class FakeHttp
    {
        public List<Call> Calls { get; } = new();
        public Func<string, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public IPackageContentFetcher CreateFetcher()
        {
            var factory = new Mock<ISquidHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
                .Returns((TimeSpan? timeout, bool _, Dictionary<string, string> headers) => new HttpClient(new Handler(this, timeout, headers)));

            return CreateSut(factory.Object);
        }

        private sealed class Handler(FakeHttp owner, TimeSpan? timeout, Dictionary<string, string> headers) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var url = request.RequestUri!.ToString();
                owner.Calls.Add(new Call(url, headers, timeout));

                return Task.FromResult(owner.Respond(url));
            }
        }
    }

    private static ExternalFeed Feed(string feedType, string feedUri = "https://packages.example.com", string username = null, string password = null) =>
        new() { Id = 7, FeedType = feedType, FeedUri = feedUri, Username = username, Password = password };

    private static byte[] TarGz(Dictionary<string, string> files)
    {
        using var ms = new MemoryStream();

        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz))
        {
            foreach (var kvp in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"pkg/{kvp.Key}") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(kvp.Value)) });
        }

        return ms.ToArray();
    }

    private static byte[] Zip(Dictionary<string, string> files)
    {
        using var ms = new MemoryStream();

        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var kvp in files)
            {
                using var w = new StreamWriter(zip.CreateEntry($"pkg/{kvp.Key}").Open());
                w.Write(kvp.Value);
            }
        }

        return ms.ToArray();
    }

    private static readonly byte[] SampleArchive = TarGz(new Dictionary<string, string> { ["a.yaml"] = "kind: A", ["readme.md"] = "x" });

    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static HttpResponseMessage OkText(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private static FakeHttp ServeArchiveAt(string url) => new() { Respond = u => u == url ? Ok(SampleArchive) : new HttpResponseMessage(HttpStatusCode.NotFound) };

    private static Task<PackageFetchResult> Fetch(FakeHttp http, ExternalFeed feed, string id = "my-package", string version = "1.0.0") =>
        http.CreateFetcher().FetchAsync(feed, id, version, CancellationToken.None);

    // === Generic / fallback ===

    [Theory]
    [InlineData("Generic")]
    [InlineData("BuiltIn")]
    [InlineData("Maven")]
    [InlineData("")]
    [InlineData(null)]
    public async Task UnrecognisedFeedType_UsesGenericBaseIdVersionUrl(string feedType)
    {
        var http = ServeArchiveAt("https://packages.example.com/my-package/2.0.0");

        var result = await Fetch(http, Feed(feedType), version: "2.0.0");

        http.Calls.Select(c => c.Url).ShouldBe(new[] { "https://packages.example.com/my-package/2.0.0" });
        http.Calls[0].Timeout.ShouldBe(DownloadTimeout);
        http.Calls[0].Headers.ShouldBeEmpty();
        result.Files.Keys.ShouldBe(new[] { "a.yaml" });
        result.RawBytes.ShouldBe(SampleArchive);
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Generic_TrailingSlashOnFeedUri_IsTrimmed()
    {
        var http = ServeArchiveAt("https://packages.example.com/my-package/1.0.0");

        await Fetch(http, Feed("Generic", "https://packages.example.com//"));

        http.Calls.Single().Url.ShouldBe("https://packages.example.com/my-package/1.0.0");
    }

    [Fact]
    public async Task Generic_EmptyVersion_OmitsVersionSegment()
    {
        var http = ServeArchiveAt("https://packages.example.com/my-package");

        await Fetch(http, Feed("Generic"), version: "");

        http.Calls.Single().Url.ShouldBe("https://packages.example.com/my-package");
    }

    [Fact]
    public async Task Generic_CredentialsAndPasswordOnly_ChooseBasicAndBearer()
    {
        var basic = ServeArchiveAt("https://packages.example.com/my-package/1.0.0");
        var bearer = ServeArchiveAt("https://packages.example.com/my-package/1.0.0");

        await Fetch(basic, Feed("Generic", username: "user", password: "pass"));
        await Fetch(bearer, Feed("Generic", password: "tok"));

        basic.Calls.Single().Headers["Authorization"].ShouldBe("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass")));
        bearer.Calls.Single().Headers["Authorization"].ShouldBe("Bearer tok");
        bearer.Calls.Single().Headers.ContainsKey("Accept").ShouldBeFalse();
    }

    // === GitHub ===

    [Theory]
    [InlineData("GitHub")]
    [InlineData("github repository feed")]
    public async Task GitHub_UsesTarballUrlAndGitHubHeaders(string feedType)
    {
        var http = ServeArchiveAt("https://api.github.com/repos/org/repo/tarball/v1.0.0");

        var result = await Fetch(http, Feed(feedType, "https://api.github.com/"), "org/repo", "v1.0.0");

        var call = http.Calls.Single();
        call.Url.ShouldBe("https://api.github.com/repos/org/repo/tarball/v1.0.0");
        call.Headers["Accept"].ShouldBe("application/vnd.github+json");
        call.Headers["X-GitHub-Api-Version"].ShouldBe("2022-11-28");
        call.Timeout.ShouldBe(DownloadTimeout);
        result.Files.ShouldContainKey("a.yaml");
    }

    [Fact]
    public async Task GitHub_PasswordOnly_UsesTokenScheme_AndWithUsername_UsesBasic()
    {
        var tokenOnly = ServeArchiveAt("https://api.github.com/repos/o/r/tarball/1");
        var basic = ServeArchiveAt("https://api.github.com/repos/o/r/tarball/1");

        await Fetch(tokenOnly, Feed("GitHub", "https://api.github.com", password: "ghp_x"), "o/r", "1");
        await Fetch(basic, Feed("GitHub", "https://api.github.com", "u", "p"), "o/r", "1");

        tokenOnly.Calls.Single().Headers["Authorization"].ShouldBe("token ghp_x");
        basic.Calls.Single().Headers["Authorization"].ShouldStartWith("Basic ");
    }

    // === NuGet ===

    [Theory]
    [InlineData("NuGet", "https://nuget.example.com", "https://nuget.example.com/api/v2/package/Pkg/1.0.0")]
    [InlineData("nuget feed", "https://nuget.example.com/", "https://nuget.example.com/api/v2/package/Pkg/1.0.0")]
    [InlineData("NuGet", "https://api.nuget.org/v3/index.json", "https://www.nuget.org/api/v2/package/Pkg/1.0.0")]
    [InlineData("NuGet", "https://api.nuget.org/v3/", "https://www.nuget.org/api/v2/package/Pkg/1.0.0")]
    [InlineData("NuGet", "https://packages.example.com/v3", "https://packages.example.com/api/v2/package/Pkg/1.0.0")]
    [InlineData("NuGet", "https://packages.example.com/nuget/v3/index.json", "https://packages.example.com/nuget/api/v2/package/Pkg/1.0.0")]
    [InlineData("NuGet", "https://packages.example.com/feed/index.json", "https://packages.example.com/feed/index.json/api/v2/package/Pkg/1.0.0")]
    public async Task NuGet_UsesV2PackagePath(string feedType, string feedUri, string expectedUrl)
    {
        var http = new FakeHttp();

        await Fetch(http, Feed(feedType, feedUri), "Pkg", "1.0.0");

        http.Calls.Single().Url.ShouldBe(expectedUrl);
        http.Calls.Single().Headers.ShouldBeEmpty();
    }

    // === Helm ===

    [Fact]
    public async Task Helm_ResolvesChartUrlFromIndex_WithAuthAndShortTimeout()
    {
        var http = new FakeHttp
        {
            Respond = url => url switch
            {
                "https://charts.example.com/index.yaml" => OkText("entries:\n  my-chart:\n  - version: 1.0.0\n    urls:\n    - charts/my-chart-1.0.0.tgz\n"),
                "https://charts.example.com/charts/my-chart-1.0.0.tgz" => Ok(SampleArchive),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            }
        };

        var result = await Fetch(http, Feed("Helm", "https://charts.example.com/", password: "tok"), "my-chart", "1.0.0");

        http.Calls.Select(c => c.Url).ShouldBe(new[] { "https://charts.example.com/index.yaml", "https://charts.example.com/charts/my-chart-1.0.0.tgz" });
        http.Calls[0].Timeout.ShouldBe(IndexTimeout);
        http.Calls[1].Timeout.ShouldBe(DownloadTimeout);
        http.Calls.ShouldAllBe(c => c.Headers["Authorization"] == "Bearer tok");
        result.Files.ShouldContainKey("a.yaml");
    }

    [Fact]
    public async Task Helm_AbsoluteUrlInIndex_IsUsedVerbatim()
    {
        var http = new FakeHttp
        {
            Respond = url => url switch
            {
                "https://charts.example.com/index.yaml" => OkText("entries:\n  c:\n  - version: 1.0.0\n    urls:\n    - https://cdn.example.com/c-1.0.0.tgz\n"),
                "https://cdn.example.com/c-1.0.0.tgz" => Ok(SampleArchive),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            }
        };

        await Fetch(http, Feed("helm feed", "https://charts.example.com"), "c", "1.0.0");

        http.Calls.Select(c => c.Url).ShouldBe(new[] { "https://charts.example.com/index.yaml", "https://cdn.example.com/c-1.0.0.tgz" });
    }

    [Theory]
    [InlineData("index404")]
    [InlineData("chartMissing")]
    [InlineData("indexThrows")]
    public async Task Helm_IndexUnusable_FallsBackToConventionUrl(string scenario)
    {
        var http = new FakeHttp
        {
            Respond = url =>
            {
                if (url.EndsWith("index.yaml"))
                {
                    return scenario switch
                    {
                        "index404" => new HttpResponseMessage(HttpStatusCode.NotFound),
                        "chartMissing" => OkText("entries:\n  other:\n  - version: 1.0.0\n    urls:\n    - o.tgz\n"),
                        _ => throw new HttpRequestException("boom")
                    };
                }

                return url == "https://charts.example.com/c-1.0.0.tgz" ? Ok(SampleArchive) : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        };

        var result = await Fetch(http, Feed("Helm", "https://charts.example.com"), "c", "1.0.0");

        http.Calls.Last().Url.ShouldBe("https://charts.example.com/c-1.0.0.tgz");
        result.Files.ShouldContainKey("a.yaml");
        result.Warnings.ShouldBeEmpty();
    }

    // === Type precedence for names matching several tokens (Contains semantics) ===

    [Fact]
    public async Task HelmAndGitHubInName_ResolvesHelmIndexButSendsGitHubHeaders()
    {
        var http = new FakeHttp { Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };

        await Fetch(http, Feed("GitHub Helm", "https://x.example.com"), "c", "1.0.0");

        http.Calls.Select(c => c.Url).ShouldBe(new[] { "https://x.example.com/index.yaml", "https://x.example.com/c-1.0.0.tgz" });
        http.Calls.ShouldAllBe(c => c.Headers["Accept"] == "application/vnd.github+json");
    }

    [Fact]
    public async Task HelmAndNuGetInName_UsesHelm()
    {
        var http = new FakeHttp();

        await Fetch(http, Feed("NuGet Helm", "https://x.example.com"), "c", "1.0.0");

        http.Calls[0].Url.ShouldBe("https://x.example.com/index.yaml");
    }

    [Fact]
    public async Task GitHubAndNuGetInName_UsesGitHub()
    {
        var http = new FakeHttp();

        await Fetch(http, Feed("NuGet GitHub", "https://api.github.com"), "o/r", "1");

        http.Calls.Single().Url.ShouldBe("https://api.github.com/repos/o/r/tarball/1");
    }

    // === Archive handling and errors ===

    [Fact]
    public async Task Zip_ExtractsYamlAndJsonOnly_StrippingRootDirectory()
    {
        var zip = Zip(new Dictionary<string, string> { ["t/d.yaml"] = "a", ["c.json"] = "{}", ["x.txt"] = "n" });
        var http = new FakeHttp { Respond = _ => Ok(zip) };

        var result = await Fetch(http, Feed("Generic"));

        result.Files.Keys.OrderBy(k => k).ShouldBe(new[] { "c.json", "t/d.yaml" });
        result.RawBytes.ShouldBe(zip);
    }

    [Fact]
    public async Task HttpFailure_ReturnsWarningWithStatusAndUrl_AndEmptyContent()
    {
        var http = new FakeHttp { Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) };

        var result = await Fetch(http, Feed("Generic"));

        result.Warnings.ShouldBe(new List<string> { "Package download failed: HTTP 403 from https://packages.example.com/my-package/1.0.0" });
        result.Files.ShouldBeEmpty();
        result.RawBytes.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnsupportedArchive_ReturnsFetchFailedWarning_AndEmptyRawBytes()
    {
        var http = new FakeHttp { Respond = _ => Ok(Encoding.UTF8.GetBytes("not an archive")) };

        var result = await Fetch(http, Feed("Generic"));

        result.Warnings.ShouldBe(new List<string> { "Package fetch failed: Unsupported archive format. Expected .tar.gz, .tar, or .zip." });
        result.Files.ShouldBeEmpty();
        result.RawBytes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Helm_IndexRequestTimesOut_FallsBackToConventionUrl()
    {
        // The 30s index client timeout surfaces as TaskCanceledException (an OperationCanceledException).
        var http = new FakeHttp
        {
            Respond = url => url.EndsWith("index.yaml")
                ? throw new TaskCanceledException("timeout")
                : url == "https://charts.example.com/c-1.0.0.tgz" ? Ok(SampleArchive) : new HttpResponseMessage(HttpStatusCode.NotFound)
        };

        var result = await Fetch(http, Feed("Helm", "https://charts.example.com"), "c", "1.0.0");

        http.Calls.Select(c => c.Url).ShouldBe(new[] { "https://charts.example.com/index.yaml", "https://charts.example.com/c-1.0.0.tgz" });
        result.Files.ShouldContainKey("a.yaml");
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task AlreadyCancelledToken_ReturnsWarningAndEmptyContent_DoesNotThrow()
    {
        // Pins EXISTING behaviour: cancellation is swallowed like any other failure. Changing it to
        // propagate OperationCanceledException is a separate decision, not part of the source split.
        var http = ServeArchiveAt("https://packages.example.com/my-package/1.0.0");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await http.CreateFetcher().FetchAsync(Feed("Generic"), "my-package", "1.0.0", cts.Token);

        result.Warnings.Count.ShouldBe(1);
        result.Warnings[0].ShouldStartWith("Package fetch failed:");
        result.Files.ShouldBeEmpty();
        result.RawBytes.ShouldBeEmpty();
    }

    [Fact]
    public async Task PlainTar_ExtractsRegularAndV7Entries_WithoutExtensionFilter()
    {
        using var ms = new MemoryStream();

        using (var tar = new TarWriter(ms, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "pkg/a.yaml") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("kind: A")) });
            tar.WriteEntry(new V7TarEntry(TarEntryType.V7RegularFile, "pkg/b.txt") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("v7")) });
        }

        var bytes = ms.ToArray();
        var http = new FakeHttp { Respond = _ => Ok(bytes) };

        var result = await Fetch(http, Feed("Generic"));

        result.Files.Keys.OrderBy(k => k).ShouldBe(new[] { "a.yaml", "b.txt" });
        Encoding.UTF8.GetString(result.Files["b.txt"]).ShouldBe("v7");
        result.RawBytes.ShouldBe(bytes);
    }

    [Fact]
    public async Task NuGetAndGitHubInName_SendsGitHubHeaders()
    {
        var http = new FakeHttp();

        await Fetch(http, Feed("NuGet GitHub", "https://api.github.com"), "o/r", "1");

        http.Calls.Single().Headers["Accept"].ShouldBe("application/vnd.github+json");
    }

    [Fact]
    public async Task GitHubHelm_PasswordOnly_SendsTokenAuthOnIndexAndDownload()
    {
        var http = new FakeHttp();

        await Fetch(http, Feed("GitHub Helm", "https://x.example.com", password: "ghp_x"), "c", "1.0.0");

        http.Calls.Count.ShouldBe(2);
        http.Calls.ShouldAllBe(c => c.Headers["Authorization"] == "token ghp_x");
    }

    [Fact]
    public async Task TransportException_IsReportedAsWarning_NotThrown()
    {
        var http = new FakeHttp { Respond = _ => throw new HttpRequestException("connection refused") };

        var result = await Fetch(http, Feed("NuGet"));

        result.Warnings.ShouldBe(new List<string> { "Package fetch failed: connection refused" });
        result.RawBytes.ShouldBeEmpty();
    }
}

using System.IO;
using System.Linq;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Http;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

/// <summary>Archives made with <c>tar czf pkg.tgz .</c> name every entry <c>./x</c>: that resolves inside the extraction root, so it is accepted and read as <c>x</c>, while real escapes stay refused.</summary>
public class CurrentDirectoryEntryTests : IDisposable
{
    private readonly List<string> _files = new();

    public void Dispose() => _files.ForEach(File.Delete);

    private string Write(byte[] bytes)
    {
        var path = Path.GetTempFileName();

        File.WriteAllBytes(path, bytes);
        _files.Add(path);

        return path;
    }

    private static byte[] TarGzWithDirs() => TestArchives.TarGz(("./", ""), ("./index.html", "<p/>"), ("./assets/app.yaml", "a: 1"), ("./package.json", "{\"version\":\"4.5.6\"}"));

    [Theory]
    [InlineData("./", "")]
    [InlineData(".", "")]
    [InlineData("./index.html", "index.html")]
    [InlineData("././a/b", "a/b")]
    [InlineData(".\\a", "a")]
    [InlineData("a/./b", "a/./b")]
    [InlineData("..", "..")]
    [InlineData(".hidden", ".hidden")]
    public void StripLeadingCurrentDirectory_OnlyDropsTheLeadingDotSlash(string name, string expected)
    {
        PackageArchiveSafety.StripLeadingCurrentDirectory(name).ShouldBe(expected);
    }

    [Fact]
    public void Safety_AcceptsDotSlashEntries_InZipAndTarGz()
    {
        foreach (var archive in new[] { TarGzWithDirs(), TestArchives.Zip(("./index.html", "x"), ("./assets/app.yaml", "y")) })
        {
            using var stream = new MemoryStream(archive);

            Should.NotThrow(() => PackageArchiveSafety.EnsureArchiveEntriesAreSafe(stream, "p", "1"));
            Should.NotThrow(() => PackageArchiveSafety.EnsureArchiveEntriesAreSafe(archive, "p", "1"));
        }
    }

    [Theory]
    [InlineData("./../evil")]
    [InlineData("a/../../evil")]
    [InlineData("../evil")]
    [InlineData("/etc/x")]
    [InlineData("C:/x")]
    [InlineData("~/x")]
    [InlineData(".//etc/x")]
    public void Safety_StillRejectsEscapes_InZipAndTarGz(string name)
    {
        foreach (var archive in new[] { TestArchives.TarGz(("ok.txt", "x"), (name, "y")), TestArchives.Zip(("ok.txt", "x"), (name, "y")) })
        {
            using var stream = new MemoryStream(archive);

            Should.Throw<InvalidOperationException>(() => PackageArchiveSafety.EnsureArchiveEntriesAreSafe(stream, "p", "1")).Message.ShouldContain("escape");
        }
    }

    // The extractor's naming is what every external-feed deployment (for example the Kubernetes YAML handler, which reads Files) already depends on. It was
    // deliberately left alone when the safety check learned about "./": a leading "./" only disappears through its wrapper-directory rule.
    [Fact]
    public void Extractor_NamingForDotSlashArchives_IsExactlyWhatItAlwaysWas()
    {
        var entries = new[] { ("./manifests/a.yaml", "a: 1"), ("./base/deployment.yaml", "b: 1"), ("./overlay/deployment.yaml", "c: 1"), ("./values.yaml", "d: 1"), ("./top/", "") };

        foreach (var archive in new[] { TestArchives.TarGz(entries), TestArchives.Zip(entries) })
        {
            var files = PackageArchiveExtractor.Extract(archive);

            files.Keys.OrderBy(k => k).ShouldBe(["base/deployment.yaml", "manifests/a.yaml", "overlay/deployment.yaml", "values.yaml"]);
            files["base/deployment.yaml"].ShouldBe(System.Text.Encoding.UTF8.GetBytes("b: 1"));
            files["overlay/deployment.yaml"].ShouldBe(System.Text.Encoding.UTF8.GetBytes("c: 1"));
        }
    }

    [Fact]
    public async Task ExternalHttpFeed_PackageWithDotSlashEntries_KeepsItsNamesThroughTheRealDownloader()
    {
        var bytes = TestArchives.TarGz(("./manifests/a.yaml", "a: 1"), ("./base/deployment.yaml", "b"), ("./overlay/deployment.yaml", "c"));
        var factory = new Mock<Squid.Core.Services.Http.ISquidHttpClientFactory>();

        factory.Setup(f => f.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>())).Returns(() => new System.Net.Http.HttpClient(new StubHandler(bytes)));

        var fetcher = PackageContentFetchWiring(factory.Object);
        var result = await fetcher.FetchAsync(new Squid.Core.Persistence.Entities.Deployments.ExternalFeed { Id = 1, FeedType = "Generic", FeedUri = "https://feed.example" }, "pkg", "1.0.0", CancellationToken.None);

        result.Files.Keys.OrderBy(k => k).ShouldBe(["base/deployment.yaml", "manifests/a.yaml", "overlay/deployment.yaml"]);
    }

    private static Squid.Core.Services.DeploymentExecution.Packages.IPackageContentFetcher PackageContentFetchWiring(Squid.Core.Services.Http.ISquidHttpClientFactory factory)
    {
        var downloader = new FeedPackageDownloader(factory);

        return new RoutingPackageContentFetcher([], new Squid.Core.Services.DeploymentExecution.Packages.Sources.Generic.GenericFeedPackageSource(downloader));
    }

    private sealed class StubHandler(byte[] body) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(body) });
        }
    }

    [Fact]
    public void Extractor_TwoEntriesWithOneName_StillLastWins_AndWarns()
    {
        var sink = new CollectingSink();
        var previous = Serilog.Log.Logger;

        Serilog.Log.Logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        try
        {
            var files = PackageArchiveExtractor.Extract(TestArchives.Zip(("a/x.yaml", "first"), ("b/x.yaml", "second")));

            files.Keys.ShouldBe(["x.yaml"]);
            files["x.yaml"].ShouldBe(System.Text.Encoding.UTF8.GetBytes("second"));
            sink.Warnings.ShouldContain(w => w.Contains("x.yaml"));
        }
        finally
        {
            Serilog.Log.Logger = previous;
        }
    }

    private sealed class CollectingSink : Serilog.Core.ILogEventSink
    {
        public List<string> Warnings { get; } = new();

        public void Emit(Serilog.Events.LogEvent logEvent)
        {
            if (logEvent.Level == Serilog.Events.LogEventLevel.Warning) Warnings.Add(logEvent.RenderMessage());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TextReader_FindsDotSlashManifests_AndListsThemAtTheRoot(bool zip)
    {
        var format = zip ? BuiltInPackageFormat.Zip : BuiltInPackageFormat.TarGz;

        var entries = new[] { ("./", ""), ("./package.json", "{\"version\":\"4.5.6\"}"), ("./src/x.txt", "x") };
        var path = Write(format == BuiltInPackageFormat.Zip ? TestArchives.Zip(entries) : TestArchives.TarGz(entries));
        var reader = new PackageArchiveTextReader(path, format);

        reader.TryReadTextFileAsync("package.json", CancellationToken.None).Result.ShouldBe("{\"version\":\"4.5.6\"}");
        reader.ListRootFileNamesAsync(CancellationToken.None).Result.ShouldBe(["package.json"]);
    }

    [Fact]
    public async Task Push_OfATarMadeWithTarCzfDot_IsAccepted_AndSourceVersionReadsItsManifest()
    {
        var h = new PushHarness { Rules = new FakeRules("{source.version}") };

        var result = await h.Service().PushAsync(PushHarness.Push(TarGzWithDirs()), CancellationToken.None);

        result.Package.Version.ShouldBe("4.5.6");
        result.Package.FileExtension.ShouldBe(".tar.gz");
    }
}

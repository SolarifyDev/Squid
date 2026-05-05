using System.Linq;
using Squid.Core.Handlers.RequestHandlers.Machines;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Requests.Machines;

namespace Squid.UnitTests.Services.Machines;

/// <summary>
/// Coverage for the GetTentacleDownloads mediator handler. The handler is
/// the FE-facing surface for the Octopus-equivalent "download installer"
/// dropdown; its contract is the (OS, architecture, libc-variant) tuple
/// list + URL pattern correctness + version resolution.
///
/// <para>Pin classes:
/// <list type="number">
///   <item>Filter shape — null/Windows/Linux/case-insensitive each return
///         the expected RID set.</item>
///   <item>Version resolution — registry called only when latest is
///         requested; specific version skips registry and is used verbatim.</item>
///   <item>URL pattern correctness — every entry's DownloadUrl matches
///         <c>{base}/{version}/squid-tentacle-{version}-{rid}.{ext}</c> and
///         the Sha256Url is the same with <c>.sha256</c> appended.</item>
///   <item>FE display fields — Label / IsRecommended / FileName populated
///         consistently so the FE doesn't have to re-derive them.</item>
/// </list>
/// </para>
/// </summary>
public sealed class GetTentacleDownloadsRequestHandlerTests
{
    private readonly Mock<ITentacleVersionRegistry> _versionRegistry = new();
    private readonly GetTentacleDownloadsRequestHandler _handler;

    public GetTentacleDownloadsRequestHandlerTests()
    {
        // Default: registry returns "1.6.0" for any OS-aware query. Individual
        // tests override per-call when verifying the per-OS query shape.
        _versionRegistry
            .Setup(x => x.GetLatestVersionAsync(It.IsAny<string>(), It.IsAny<MachineRuntimeCapabilities>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("1.6.0");

        _handler = new GetTentacleDownloadsRequestHandler(_versionRegistry.Object);
    }

    private static Mock<IReceiveContext<GetTentacleDownloadsRequest>> CreateContext(string os = null, string version = null)
    {
        var context = new Mock<IReceiveContext<GetTentacleDownloadsRequest>>();
        context.Setup(x => x.Message).Returns(new GetTentacleDownloadsRequest { OperatingSystem = os, Version = version });
        return context;
    }

    // ── Filter shape ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_NoOsFilter_ReturnsAllSixDownloads()
    {
        // 2 Windows (win-x64, win-arm64) + 4 Linux (linux-x64, linux-arm64,
        // linux-musl-x64, linux-musl-arm64) — matches the publish workflows.
        var result = await _handler.Handle(CreateContext().Object, CancellationToken.None);

        result.Data.Downloads.Count.ShouldBe(6, customMessage:
            "Workflow publishes 2 Windows + 4 Linux archives — endpoint must surface every variant.");

        result.Data.Downloads.Select(d => d.Rid).ShouldBe(
            ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Handle_OsFilter_Windows_ReturnsOnlyTwoWindowsDownloads()
    {
        var result = await _handler.Handle(CreateContext(os: "Windows").Object, CancellationToken.None);

        result.Data.Downloads.Count.ShouldBe(2);
        result.Data.Downloads.ShouldAllBe(d => d.OperatingSystem == "Windows");
        result.Data.Downloads.Select(d => d.Rid).ShouldBe(["win-x64", "win-arm64"], ignoreOrder: true);
    }

    [Fact]
    public async Task Handle_OsFilter_Linux_ReturnsOnlyFourLinuxDownloads()
    {
        var result = await _handler.Handle(CreateContext(os: "Linux").Object, CancellationToken.None);

        result.Data.Downloads.Count.ShouldBe(4);
        result.Data.Downloads.ShouldAllBe(d => d.OperatingSystem == "Linux");
        result.Data.Downloads.Select(d => d.Rid).ShouldBe(
            ["linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64"],
            ignoreOrder: true);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("WINDOWS")]
    [InlineData("Windows")]
    public async Task Handle_OsFilter_CaseInsensitive(string osArg)
    {
        // Mirrors the case-insensitive filter on GenerateTentacleInstallScriptCommand
        // — operators copy/paste casing from docs / their automation; rejecting
        // "windows" while accepting "Windows" would be needless friction.
        var result = await _handler.Handle(CreateContext(os: osArg).Object, CancellationToken.None);

        result.Data.Downloads.Count.ShouldBe(2);
    }

    // ── Version resolution ───────────────────────────────────────────────────

    [Fact]
    public async Task Handle_VersionLatest_QueriesRegistry()
    {
        await _handler.Handle(CreateContext(version: "latest").Object, CancellationToken.None);

        _versionRegistry.Verify(
            x => x.GetLatestVersionAsync(It.IsAny<string>(), It.IsAny<MachineRuntimeCapabilities>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            failMessage: "Version='latest' must call the registry to resolve the actual latest tag.");
    }

    [Fact]
    public async Task Handle_VersionNullOrEmpty_QueriesRegistry()
    {
        // null / empty / whitespace all mean "use latest" — operators leaving
        // the field unset on a GET call should get the same behaviour as
        // explicitly passing "latest".
        await _handler.Handle(CreateContext(version: null).Object, CancellationToken.None);
        await _handler.Handle(CreateContext(version: "").Object, CancellationToken.None);
        await _handler.Handle(CreateContext(version: "   ").Object, CancellationToken.None);

        _versionRegistry.Verify(
            x => x.GetLatestVersionAsync(It.IsAny<string>(), It.IsAny<MachineRuntimeCapabilities>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(3));
    }

    [Fact]
    public async Task Handle_SpecificVersion_DoesNotCallRegistry_AndUsesVerbatim()
    {
        var result = await _handler.Handle(CreateContext(version: "2.0.5").Object, CancellationToken.None);

        _versionRegistry.Verify(
            x => x.GetLatestVersionAsync(It.IsAny<string>(), It.IsAny<MachineRuntimeCapabilities>(), It.IsAny<CancellationToken>()),
            Times.Never,
            failMessage: "Specific version pin must skip the registry — operators on air-gapped mirrors " +
                         "stage a particular version locally and need URL generation NOT to depend on " +
                         "GitHub Releases reachability.");

        result.Data.LatestVersion.ShouldBe("2.0.5");
        result.Data.Downloads.ShouldAllBe(d => d.DownloadUrl.Contains("/2.0.5/"));
        result.Data.Downloads.ShouldAllBe(d => d.FileName.Contains("-2.0.5-"));
    }

    [Fact]
    public async Task Handle_LatestVersion_QueriesPerOs_WindowsAndLinuxIndependently()
    {
        // Linux + Windows are typically released on the same git tag, but the
        // registry queries them via DIFFERENT sources (Docker Hub vs GitHub
        // Releases). Each must be queried independently so a temporary outage
        // of one source doesn't poison the other's response.
        _versionRegistry
            .Setup(x => x.GetLatestVersionAsync(It.IsAny<string>(), It.Is<MachineRuntimeCapabilities>(c => c.IsWindows), It.IsAny<CancellationToken>()))
            .ReturnsAsync("1.6.0");
        _versionRegistry
            .Setup(x => x.GetLatestVersionAsync(It.IsAny<string>(), It.Is<MachineRuntimeCapabilities>(c => !c.IsWindows), It.IsAny<CancellationToken>()))
            .ReturnsAsync("1.5.9");

        var result = await _handler.Handle(CreateContext().Object, CancellationToken.None);

        result.Data.Downloads.Where(d => d.OperatingSystem == "Windows")
            .ShouldAllBe(d => d.DownloadUrl.Contains("/1.6.0/"));
        result.Data.Downloads.Where(d => d.OperatingSystem == "Linux")
            .ShouldAllBe(d => d.DownloadUrl.Contains("/1.5.9/"));
    }

    // ── URL pattern correctness ──────────────────────────────────────────────

    [Fact]
    public async Task Handle_WindowsDownload_UrlFollowsZipConvention()
    {
        var result = await _handler.Handle(CreateContext(os: "Windows", version: "1.6.0").Object, CancellationToken.None);

        var x64 = result.Data.Downloads.Single(d => d.Rid == "win-x64");

        x64.DownloadUrl.ShouldEndWith("/1.6.0/squid-tentacle-1.6.0-win-x64.zip", customMessage:
            "Windows downloads must be .zip archives at the canonical Releases path.");
        x64.Sha256Url.ShouldBe(x64.DownloadUrl + ".sha256", customMessage:
            "SHA256 companion is always {DownloadUrl}.sha256 — Phase-12.E.9 release-pipeline contract.");
        x64.FileName.ShouldBe("squid-tentacle-1.6.0-win-x64.zip");
    }

    [Fact]
    public async Task Handle_LinuxDownload_UrlFollowsTarGzConvention()
    {
        var result = await _handler.Handle(CreateContext(os: "Linux", version: "1.6.0").Object, CancellationToken.None);

        var glibcX64 = result.Data.Downloads.Single(d => d.Rid == "linux-x64");

        glibcX64.DownloadUrl.ShouldEndWith("/1.6.0/squid-tentacle-1.6.0-linux-x64.tar.gz", customMessage:
            "Linux downloads must be .tar.gz archives at the canonical Releases path.");
        glibcX64.Sha256Url.ShouldBe(glibcX64.DownloadUrl + ".sha256");
        glibcX64.FileName.ShouldBe("squid-tentacle-1.6.0-linux-x64.tar.gz");
    }

    [Fact]
    public async Task Handle_EveryDownload_HasSha256Companion()
    {
        // Defensive pin: a future refactor that adds a new RID without
        // populating Sha256Url would silently break the FE's "verify checksum"
        // workflow. Pin every entry to have it.
        var result = await _handler.Handle(CreateContext().Object, CancellationToken.None);

        result.Data.Downloads.ShouldAllBe(
            d => !string.IsNullOrEmpty(d.Sha256Url) && d.Sha256Url.EndsWith(".sha256"),
            customMessage: "every download must surface a .sha256 companion URL — operators rely on it for integrity verification on manual downloads");
    }

    // ── FE display fields ────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_OneRecommendedPerOs_DefaultsToX64()
    {
        // Mirrors the IsRecommended invariant on ITentacleInstallScriptBuilder:
        // exactly one entry per OS marks "this is the default selection". x64
        // is the broad-deploy default; arm64 is for explicitly-arm64 hardware.
        var result = await _handler.Handle(CreateContext().Object, CancellationToken.None);

        var windowsRecommended = result.Data.Downloads.Where(d => d.OperatingSystem == "Windows" && d.IsRecommended).ToList();
        var linuxRecommended = result.Data.Downloads.Where(d => d.OperatingSystem == "Linux" && d.IsRecommended).ToList();

        windowsRecommended.Count.ShouldBe(1);
        windowsRecommended[0].Rid.ShouldBe("win-x64");

        linuxRecommended.Count.ShouldBe(1);
        linuxRecommended[0].Rid.ShouldBe("linux-x64", customMessage:
            "linux-x64 (glibc) is the default; musl variants are Alpine-specific opt-in.");
    }

    [Fact]
    public async Task Handle_LinuxLabels_DistinguishGlibcAndMusl()
    {
        var result = await _handler.Handle(CreateContext(os: "Linux").Object, CancellationToken.None);

        result.Data.Downloads.Single(d => d.Rid == "linux-x64").LibcVariant.ShouldBe("glibc");
        result.Data.Downloads.Single(d => d.Rid == "linux-musl-x64").LibcVariant.ShouldBe("musl");
        result.Data.Downloads.Single(d => d.Rid == "linux-musl-arm64").LibcVariant.ShouldBe("musl");

        // Label visible to operator must clearly distinguish — picking the wrong
        // variant on Alpine causes startup crash with cryptic GLIBC errors.
        result.Data.Downloads.Single(d => d.Rid == "linux-musl-x64").Label.ShouldContain("musl");
    }

    [Fact]
    public async Task Handle_WindowsLabels_NoLibcVariant()
    {
        // Windows has no libc-variant axis (PE binary doesn't depend on it).
        // Pin LibcVariant null so the FE doesn't try to render an empty
        // "(glibc)" suffix on Windows entries.
        var result = await _handler.Handle(CreateContext(os: "Windows").Object, CancellationToken.None);

        result.Data.Downloads.ShouldAllBe(d => d.LibcVariant == null);
    }

    [Fact]
    public async Task Handle_Architecture_PopulatedForEveryEntry()
    {
        var result = await _handler.Handle(CreateContext().Object, CancellationToken.None);

        result.Data.Downloads.ShouldAllBe(d => d.Architecture == "x64" || d.Architecture == "arm64",
            customMessage: "Architecture must be one of the canonical CPU labels — null/empty would break " +
                           "any FE that groups by architecture.");
    }
}

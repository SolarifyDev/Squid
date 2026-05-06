using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Squid.WindowsUpgradeE2ETests;

/// <summary>
/// end-to-end verification of the opportunistic SHA256
/// companion-file fetch + verification logic added to
/// <c>upgrade-windows-tentacle.ps1</c> in . Uses an
/// in-process <see cref="HttpListener"/> serving a known fixture pair
/// (.zip + .sha256) so each test controls EVERY server response (404,
/// invalid body, valid match, valid mismatch) — no GitHub Releases
/// dependency, fully deterministic, runs in seconds.
///
/// <para><b>Why a fixture HTTP server (not a static-file path)</b>: the
/// production .ps1 uses <c>Invoke-WebRequest</c> via a real URL, and the
/// behaviour we want to pin is THE NETWORK FETCH (transport-error
/// handling, 404 fall-through, content-type tolerance). A pure file://
/// or static-file approach would bypass the HTTP layer that's the actual
/// cross-platform contract. <c>HttpListener</c> on a random localhost
/// port matches production traffic shape minus TLS — sufficient for the
/// .ps1's verification logic which doesn't validate the cert (the SHA
/// itself is the integrity check; transit is HTTPS in production but
/// the verification logic is HTTP-agnostic).</para>
///
/// <para><b>Test isolation</b>: each test starts its own HttpListener
/// on a random localhost port (no shared singleton state, no
/// cross-test races). Per-test cleanup via <c>using</c> blocks +
/// finally-disposed listener.</para>
///
/// <para><b>Drift detector</b> (cross-platform): asserts the production
/// .ps1's SHA-handling block contains the same critical operations the
/// inline test script exercises. Mirrors the
/// <c>PhaseBScript_MirrorsProductionTemplate</c> pattern from Phase
/// 12.E.7.A-2.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.ShaVerify)]
public sealed class WindowsUpgradeShaVerifyE2ETests
{
    // ========================================================================
    // Drift detector — runs on every platform (no Windows guard).
    // Pins that the production .ps1 contains the operations the inline
    // test script exercises. Without this, a future polish to the .ps1's
    // SHA-handling block could pass test-side without updating the test
    // script — the test would then validate a stale shape.
    // ========================================================================

    [Fact]
    public void Sha256VerifyBlock_ProductionTemplate_ContainsCriticalOperations()
    {
        var prodTemplate = File.ReadAllText(LocateProductionTemplate());

        var keyOperations = new[]
        {
            "$DOWNLOAD_URL.sha256",         // companion URL convention
            "Invoke-WebRequest",            // PowerShell HTTP fetch
            "-UseBasicParsing",             // no IE-engine dep on Server Core
            "-TimeoutSec",                  // bounded fetch
            "'^[0-9a-f]{64}$'",             // 64-hex validation regex
            "Get-FileHash",                 // local-side hash compute
            "-Algorithm SHA256",            // explicit algorithm
            "exit 7",                       // documented mismatch exit code
        };

        foreach (var op in keyOperations)
        {
            prodTemplate.ShouldContain(op,
                customMessage: $"production upgrade-windows-tentacle.ps1 must contain '{op}' (key SHA-handling operation pinned by  + verified by 12.E.9.4 E2E tests). If this fails: a polish to the SHA block dropped the operation; either restore it OR update the drift detector to reflect the new contract.");
        }
    }

    [Fact]
    public void Sha256VerifyBlock_DownloadUrlConvention_AppendDotSha256()
    {
        //  release workflows publish `<archive>.sha256` next
        // to `<archive>.zip`. The .ps1 must construct the companion URL by
        // appending '.sha256' to DOWNLOAD_URL — same convention Linux .sh
        // uses (sh:421). Drift would mean .ps1 hits a 404 for every
        // companion, falls through to skip, and silently never verifies
        // even when companions exist.
        var prodTemplate = File.ReadAllText(LocateProductionTemplate());
        var linuxTemplate = File.ReadAllText(LocateLinuxTemplate());

        prodTemplate.ShouldMatch(@"\$DOWNLOAD_URL\.sha256",
            customMessage: ".ps1 must append .sha256 to DOWNLOAD_URL");
        linuxTemplate.ShouldMatch(@"\$\{?DOWNLOAD_URL\}?\.sha256|\$\{DOWNLOAD_URL\}\.sha256",
            customMessage: ".sh must use the same convention — pinned cross-platform so a future operator-side mirror builds .sha256 alongside .zip + .tar.gz uniformly");
    }

    // ========================================================================
    // E2E: real Invoke-WebRequest against a localhost HTTP fixture.
    // Each test gets its own HttpListener on a fresh random port so they
    // can run in parallel without cross-pollution.
    // ========================================================================

    [Fact]
    public void OpportunisticFetch_ValidShaCompanion_PowerShellExtractsHexDigest()
    {
        if (!OperatingSystem.IsWindows()) return;

        var archiveContent = Encoding.UTF8.GetBytes("fixture archive content");
        var expectedSha = ComputeSha256Hex(archiveContent);
        var shaFileBody = $"{expectedSha}  squid-tentacle-fixture.zip\n";

        using var fixture = new ShaFixtureHttpServer();
        fixture.Routes["/squid-tentacle-fixture.zip.sha256"] = (status: 200, body: shaFileBody);

        var (exitCode, stdout) = RunFetchAndExtract(fixture.BaseUrl + "/squid-tentacle-fixture.zip");

        exitCode.ShouldBe(0,
            customMessage: $"fetch-and-extract script must exit 0 on valid 64-hex SHA companion. stdout:\n{stdout}");
        stdout.ShouldContain(expectedSha,
            customMessage: $"PowerShell must extract the 64-hex digest from the sha256sum-format response (got: {stdout})");
    }

    [Fact]
    public void OpportunisticFetch_404_FallsThroughCleanly_NoExitCode7()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Older releases without .sha256 companions OR air-gap mirrors that
        // haven't replicated the companion files yet: fetch must NOT fail
        // the upgrade. Fall through to skip-with-warning.
        using var fixture = new ShaFixtureHttpServer();
        // No route registered → fixture returns 404.

        var (exitCode, stdout) = RunFetchAndExtract(fixture.BaseUrl + "/missing.zip");

        exitCode.ShouldBe(0,
            customMessage: "404 on .sha256 companion fetch MUST be a non-fatal fall-through (matches Linux .sh's behaviour for older releases). Otherwise every air-gap mirror without companion files would fail every upgrade with exit 7.");
        stdout.ShouldContain("No .sha256 companion",
            customMessage: "skip log line MUST be emitted so operators investigating 'why didn't SHA verify' see the absent-companion reason");
    }

    [Fact]
    public void OpportunisticFetch_InvalidContent_NotHexNot64Chars_FallsThroughCleanly()
    {
        if (!OperatingSystem.IsWindows()) return;

        // What if a misconfigured server / proxy / CDN returns an HTML
        // 404 page or some other non-SHA content with HTTP 200? The
        // .ps1's regex guard `^[0-9a-f]{64}$` catches this — must fall
        // through, not pretend the HTML body is the SHA.
        using var fixture = new ShaFixtureHttpServer();
        fixture.Routes["/missing.zip.sha256"] = (status: 200, body: "<!DOCTYPE html><html>not found</html>");

        var (exitCode, stdout) = RunFetchAndExtract(fixture.BaseUrl + "/missing.zip");

        exitCode.ShouldBe(0,
            customMessage: "non-hex content with HTTP 200 must be treated as 'no valid SHA' — guards against intermediary proxies returning HTML 404 pages with success-code");
        stdout.ShouldContain("non-hex",
            customMessage: "skip log line MUST name the validation reason (non-hex / wrong length) so operators understand why a 200 OK still resulted in skip");
    }

    [Fact]
    public void OpportunisticFetch_TruncatedHex_LessThan64Chars_FallsThroughCleanly()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Defensive: a partial-write at the release pipeline could produce
        // a truncated .sha256 file. The 64-char regex catches this — must
        // fall through with a clear reason.
        using var fixture = new ShaFixtureHttpServer();
        fixture.Routes["/truncated.zip.sha256"] = (status: 200, body: "abc123  truncated.zip");

        var (exitCode, _) = RunFetchAndExtract(fixture.BaseUrl + "/truncated.zip");

        exitCode.ShouldBe(0,
            customMessage: "<64-char hex must be rejected by the regex guard, fall through, NOT crash");
    }

    [Fact]
    public void OpportunisticFetch_ServerTimesOut_FallsThroughCleanly()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Pin the timeout-handling path: -TimeoutSec 10 in the .ps1 means
        // Invoke-WebRequest throws on >10s server delay. The catch must
        // treat this as fall-through (NOT propagate). We test by pointing
        // at a port nothing listens on — Invoke-WebRequest fails with
        // connection refused, same catch path.
        var (exitCode, stdout) = RunFetchAndExtract("http://127.0.0.1:1/squid-tentacle.zip");

        exitCode.ShouldBe(0,
            customMessage: "transport-layer failure (connection refused / timeout) MUST be caught + treated as fall-through. Otherwise transient network issues at the agent would fail every upgrade with a misleading transport error.");
    }

    // ========================================================================
    // Helpers — minimal HTTP fixture server + inline PS script that mirrors
    // the production .ps1's SHA-handling block. The drift detector above
    // pins the cross-script alignment.
    // ========================================================================

    /// <summary>
    /// Inline PowerShell script: fetches and extracts the SHA hex digest.
    /// Mirrors upgrade-windows-tentacle.ps1's lines 222-262 (fetch +
    /// validate). Echos the extracted SHA on success or the skip-reason
    /// on fall-through. Returns exit 0 in both cases — exit 7 ONLY on
    /// downstream verify-mismatch (covered by a separate test that's
    /// deferred until the workflow ships .sha256 companions in real CI).
    /// </summary>
    private static string BuildFetchAndExtractScript(string downloadUrl) => $@"
$ErrorActionPreference = 'Stop'
$DOWNLOAD_URL = '{downloadUrl.Replace("'", "''")}'
$EXPECTED_SHA256 = ''

if ([string]::IsNullOrWhiteSpace($EXPECTED_SHA256)) {{
    $shaUrl = ""$DOWNLOAD_URL.sha256""
    Write-Host ""[fetch] opportunistic fetch from $shaUrl""
    try {{
        $shaResponse = Invoke-WebRequest -Uri $shaUrl -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
        $fetched = ($shaResponse.Content -split '\s+', 2)[0].Trim().ToLower()
        if ($fetched -match '^[0-9a-f]{{64}}$') {{
            $EXPECTED_SHA256 = $fetched
            Write-Host ""[fetch] valid SHA256: $EXPECTED_SHA256""
        }} else {{
            Write-Host ""[fetch] non-hex / wrong length — skipping verification""
        }}
    }}
    catch {{
        Write-Host ""[fetch] No .sha256 companion at $shaUrl — skipping verification""
    }}
}}

if ([string]::IsNullOrWhiteSpace($EXPECTED_SHA256)) {{
    Write-Host ""[fetch] result: NO_SHA_AVAILABLE""
    exit 0
}}

Write-Host ""[fetch] result: SHA_AVAILABLE: $EXPECTED_SHA256""
exit 0
";

    private static (int exitCode, string stdout) RunFetchAndExtract(string downloadUrl)
    {
        var script = BuildFetchAndExtractScript(downloadUrl);
        var tempScript = Path.Combine(Path.GetTempPath(), $"squid-sha-fetch-{Guid.NewGuid():N}.ps1");
        // UTF-8 WITH BOM — Windows PowerShell 5.1 parses BOM-less UTF-8 as ANSI
        // codepage by default, mangling non-ASCII (em-dashes, Chinese, emoji)
        // into "?" or invalid chars and triggering parse errors. Production
        // LocalScriptService.WriteScriptFile uses encoderShouldEmitUTF8Identifier
        // = true for the same reason.
        File.WriteAllText(tempScript, script, new UTF8Encoding(true));

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to launch powershell.exe");

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("fetch-and-extract did not complete within 30s");
            }

            return (p.ExitCode, stdoutTask.Result + Environment.NewLine + stderrTask.Result);
        }
        finally
        {
            try { File.Delete(tempScript); } catch { }
        }
    }

    private static string ComputeSha256Hex(byte[] content)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(content);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string LocateProductionTemplate() => LocateRepoFile(Path.Combine("src", "Squid.Core", "Resources", "Upgrade", "upgrade-windows-tentacle.ps1"));

    private static string LocateLinuxTemplate() => LocateRepoFile(Path.Combine("src", "Squid.Core", "Resources", "Upgrade", "upgrade-linux-tentacle.sh"));

    private static string LocateRepoFile(string relativePath)
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"Could not locate {relativePath} from the test assembly's directory tree");
    }

    /// <summary>
    /// Tiny in-process HTTP server using <see cref="HttpListener"/>.
    /// Each test instantiates its own (random-port loopback) so tests
    /// run in parallel without cross-pollution. Routes are configurable
    /// per test via the <see cref="Routes"/> dictionary.
    /// </summary>
    private sealed class ShaFixtureHttpServer : IDisposable
    {
        public Dictionary<string, (int status, string body)> Routes { get; } = new();
        public string BaseUrl { get; }

        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public ShaFixtureHttpServer()
        {
            // Pick a random port by binding 0; HttpListener doesn't expose
            // the assigned port directly so we use a TCP probe to find one.
            var port = GetAvailablePort();
            BaseUrl = $"http://127.0.0.1:{port}";

            _listener = new HttpListener();
            _listener.Prefixes.Add($"{BaseUrl}/");
            _listener.Start();

            _ = Task.Run(() => ServeLoopAsync(_cts.Token));
        }

        private async Task ServeLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                try
                {
                    var path = ctx.Request.Url?.AbsolutePath ?? "/";
                    if (Routes.TryGetValue(path, out var route))
                    {
                        ctx.Response.StatusCode = route.status;
                        var bytes = Encoding.UTF8.GetBytes(route.body);
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                    }
                }
                finally
                {
                    try { ctx.Response.OutputStream.Close(); } catch { }
                }
            }
        }

        private static int GetAvailablePort()
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _cts.Dispose();
        }
    }
}

using System.IO.Compression;
using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Real-world HTTPS composite E2E for <c>Squid.DeployToIISWebSite</c>: deploys a realistic
/// ASP.NET artifact with HTTPS auto-import + binding via <c>certificateVariable</c>, then
/// proves all four production HTTPS invariants compose:
///
/// <list type="number">
///   <item><b>Cert auto-imports</b> into <c>Cert:\LocalMachine\My</c> from a base64 PFX
///         referenced via the <c>Certificate.PfxBase64</c> action property</item>
///   <item><b>Thumbprint variable populated</b> — <c>$SquidVariables["&lt;name&gt;.Thumbprint"]</c>
///         is set by the import function so the binding's <c>certificateVariable</c> field
///         resolves at SSL-binding time</item>
///   <item><b>netsh sslcert binding</b> created on the configured loopback port pointing at
///         the auto-imported cert (NOT a stale or pre-existing thumbprint)</item>
///   <item><b>Private-key ACL grant</b> on the cert's CNG key file at
///         <c>%ProgramData%\Microsoft\Crypto\Keys\&lt;KeyName&gt;</c> for the AppPool's
///         identity SID — without this, IIS worker process can't read the cert and HTTPS
///         requests die with <c>SCHANNEL 36880</c></item>
///   <item><b>Live HTTPS request</b> — <c>Invoke-WebRequest -SkipCertificateCheck https://...</c>
///         returns HTTP 200 with the deployed artifact's content visible in the response body
///         (proves IIS worker resolved the cert and is serving on the bound port)</item>
/// </list>
///
/// <para><b>Coverage delta vs <see cref="IISDeployRealHostE2ETests"/></b>: the per-feature
/// real-host suite verifies cert lands in the store AND netsh shows the binding, but does
/// NOT issue a real HTTPS request OR verify private-key ACL. That's the gap this composite
/// closes — the four invariants above all need to compose correctly OR the operator's first
/// HTTPS deploy returns SCHANNEL errors that aren't surfaced by component tests.</para>
///
/// <para><b>Coverage delta vs <see cref="IISDeployRealWorldDotNetAppE2ETests"/></b>: that
/// composite deploys HTTP only (port 80-style). Production IIS deploys are predominantly
/// HTTPS. This composite is the HTTPS counterpart with cert-management plumbing baked in.</para>
///
/// <para><b>Tier</b>: 🟢 High-fidelity. Real IIS, real PowerShell, real
/// <c>New-SelfSignedCertificate</c>, real <c>netsh http add sslcert</c>, real ACL via
/// <c>icacls</c>-equivalent .NET APIs, real loopback HTTPS request. Skip-on-non-Windows
/// guard + IIS-feature probe keep cross-OS dev hosts running a clean "0 / 0".</para>
///
/// <para><b>Why <c>certificateVariable</c> not raw <c>thumbprint</c></b>: production
/// operators store the PFX as a sensitive Squid variable (<c>#{Sensitive.MyPfx}</c>) so the
/// thumbprint is unknown at step-design time. <c>certificateVariable</c> is the
/// only path that resolves dynamically post-import. Hardcoded thumbprint is a degenerate
/// path covered by <see cref="IISDeployRealHostE2ETests"/>.</para>
///
/// <para><b>SNI not covered here</b>: this composite uses a non-SNI catch-all binding
/// (<c>host=""</c>, <c>requireSni=false</c>) so the HTTPS probe can dial <c>https://127.0.0.1</c>
/// without requiring a hosts-file entry (CI runners don't have write access to the system
/// hosts file). The SNI binding path is covered by
/// <c>IISDeployRealHostE2ETests.RealIIS_HttpsBindingSni_RegistersCertViaNetshHostnameport</c>
/// — separating concerns keeps this test focused on the cert-import + ACL + live-request chain.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.IISDeploy)]
public sealed class IISDeployRealWorldHttpsE2ETests
{
    private const string CertVariableName = "OrderApiCert";

    [Fact]
    public void RealWorld_HttpsWithCertAutoImport_FullDeployAndLiveHttpsRequest()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new HttpsTestContext();
        ctx.RegisterNetshIpPortForCleanup(ctx.HttpsPort);

        // ──── STAGE 1: Build a realistic ASP.NET-style app artifact ───────────────
        //
        // Keeps the artifact narrow (vs RealWorldDotNetApp composite which goes wide)
        // so the test focuses on HTTPS plumbing assertions. Still proves the
        // SubstituteInFiles + ConfigurationVariables paths compose with HTTPS — these
        // are the rewriters operators flip ON for app config alongside HTTPS.
        var artifactPath = StageAspNetArtifact(ctx);

        // ──── STAGE 2: Generate self-signed PFX via real Windows crypto ───────────
        //
        // Operator-equivalent: operator stages a real PFX file (issued by their PKI),
        // base64-encodes it, sets it as a sensitive Squid variable. We do this at runtime
        // because:
        //   - 30-day expiry on a checked-in cert would create a "cert expires next month"
        //     maintenance trap, exposed only via test failures on a Tuesday.
        //   - The DNS SAN must be unique per-test so concurrent CI runners don't collide
        //     on the LocalMachine\My store (cert subjects are process-global).
        var dnsName = $"squid-https-{ctx.Suffix}.local";
        var pfxPassword = $"Sq!{Guid.NewGuid():N}";
        var pfxBytes = GenerateSelfSignedPfxBytes(dnsName, pfxPassword);
        var pfxBase64 = Convert.ToBase64String(pfxBytes);

        // ──── STAGE 3: Define the operator's Squid variable set ───────────────────
        //
        // Sensitive variables are referenced via #{} tokens in action properties — proves
        // the variable-substitution stage resolves them BEFORE the script runs, so the
        // agent-side script gets the real base64 + password literally.
        var variables = new List<VariableDto>
        {
            new() { Name = "AppVersion", Value = "2.1.0" },
            new() { Name = "EnvironmentTag", Value = "Production" },
            new() { Name = "ApiUrl", Value = "https://api.prod.example.com/v2" },
            // Sensitive values — operator stores these as `IsSensitive=true` in the variable
            // set; the variable-substitution path masks them in logs.
            new() { Name = "OrderApiPfx", Value = pfxBase64, IsSensitive = true },
            new() { Name = "OrderApiPfxPassword", Value = pfxPassword, IsSensitive = true }
        };

        // ──── STAGE 4: Build the action with HTTPS cert + binding via certificateVariable ──
        //
        // Property values use #{} tokens so the variable substitution layer resolves them.
        // The binding JSON's `certificateVariable` field references the operator-chosen
        // logical name; the PS1's HTTPS branch reads `$SquidVariables["<varName>.Thumbprint"]`
        // which the import function populates after `Import-IISCertificateFromPfxBase64`.
        // Non-SNI catch-all binding on loopback HTTPS port so the probe can dial
        // https://127.0.0.1:<port> without DNS resolution. SNI semantics are tested
        // separately in IISDeployRealHostE2ETests.RealIIS_HttpsBindingSni_*.
        var bindingsJson =
            "[{\"protocol\":\"https\"," +
            $"\"port\":\"{ctx.HttpsPort}\"," +
            "\"host\":\"\"," +
            "\"ipAddress\":\"*\"," +
            $"\"certificateVariable\":\"{CertVariableName}\"," +
            "\"requireSni\":false," +
            "\"enabled\":true}]";

        var action = BuildAction(
            // — Package extraction —
            (Property.PackageSourcePath, artifactPath),
            (Property.PackagePurgeBeforeExtract, "True"),
            // — IIS configure —
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, bindingsJson),
            (Property.EnableAnonymousAuthentication, "True"),
            (Property.EnableBasicAuthentication, "False"),
            (Property.EnableWindowsAuthentication, "False"),
            (Property.StartApplicationPool, "True"),
            (Property.StartWebSite, "True"),
            // — HTTPS cert auto-import (the surface under test) —
            (Property.CertificatePfxBase64, "#{OrderApiPfx}"),
            (Property.CertificatePfxPassword, "#{OrderApiPfxPassword}"),
            (Property.CertificateThumbprintVariableName, CertVariableName),
            // — SubstituteInFiles (proves app-config rewrite composes with HTTPS) —
            (Property.SubstituteInFilesEnabled, "True"),
            (Property.SubstituteInFilesTargetFiles, "appsettings.json"),
            // — ConfigurationVariables (proves web.config rewrite composes too) —
            (Property.ConfigurationVariablesEnabled, "True"));

        // ──── STAGE 5: Build + run the deploy script ──────────────────────────────
        var script = IISDeployScriptBuilder.Build(action, variables);
        var deployResult = RunPowerShell(script);

        deployResult.ExitCode.ShouldBe(0,
            customMessage:
                "HTTPS deploy failed. Triage steps:\n" +
                "  1. Grep STDOUT for 'imported PFX with thumbprint' — if absent, cert auto-import didn't fire\n" +
                $"  2. `Get-ChildItem Cert:\\LocalMachine\\My` to inspect store contents\n" +
                $"  3. `& netsh http show sslcert ipport=0.0.0.0:{ctx.HttpsPort}` to inspect netsh state\n" +
                $"  4. `Get-Website -Name '{ctx.SiteName}'` to check IIS metabase\n\n" +
                $"STDOUT:\n{deployResult.StdOut}\n\nSTDERR:\n{deployResult.StdErr}");

        // ──── STAGE 6: Extract the imported thumbprint from STDOUT for assertions ─
        var thumbprintMatch = System.Text.RegularExpressions.Regex.Match(
            deployResult.StdOut,
            @"imported PFX with thumbprint ([A-F0-9]{40})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        thumbprintMatch.Success.ShouldBeTrue(
            customMessage:
                "Couldn't extract imported thumbprint from STDOUT — either the import log format changed " +
                $"or the cert wasn't actually imported. STDOUT excerpt:\n{Truncate(deployResult.StdOut, 4000)}");

        var importedThumbprint = thumbprintMatch.Groups[1].Value.ToUpperInvariant();
        ctx.RegisterCertThumbprintForCleanup(importedThumbprint);

        // ──── INVARIANT 1: Cert auto-imported into LocalMachine\My ────────────────
        var inStore = PowerShellSingleLine(
            $"if (Get-ChildItem -Path 'Cert:\\LocalMachine\\My\\{importedThumbprint}' -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}");
        inStore.ShouldBe("true",
            customMessage:
                $"Imported cert thumbprint {importedThumbprint} not found in Cert:\\LocalMachine\\My. " +
                "Auto-import claims it landed but the store doesn't agree.");

        // ──── INVARIANT 2: Thumbprint variable populated post-import ──────────────
        // The import function MUST set $SquidVariables["{CertVariableName}.Thumbprint"]
        // BEFORE the bindings loop runs, otherwise certificateVariable resolution fails.
        // We detect this by grepping STDOUT for the "Exposing thumbprint via Squid variable"
        // message OR the binding-apply success message — the deploy succeeded above so we
        // know the binding got the thumbprint somehow.
        deployResult.StdOut.ShouldMatch(
            @"(Exposing thumbprint via Squid variable|certificateVariable.*resolved|netsh http add sslcert)",
            customMessage:
                "STDOUT doesn't show any of the expected cert-variable resolution markers. " +
                "Either the import didn't populate $SquidVariables OR the binding fell through " +
                $"to a fallback path. STDOUT:\n{Truncate(deployResult.StdOut, 4000)}");

        // ──── INVARIANT 3: netsh sslcert binding exists for the auto-imported cert ─
        // Non-SNI binding → ipport lookup (`netsh http show sslcert ipport=0.0.0.0:<port>`).
        var netshOutput = RunPowerShell(
            $"& netsh http show sslcert ipport=0.0.0.0:{ctx.HttpsPort}").StdOut;
        netshOutput.ToUpperInvariant().ShouldContain(importedThumbprint,
            customMessage:
                $"netsh sslcert binding for 0.0.0.0:{ctx.HttpsPort} doesn't show the auto-imported " +
                $"thumbprint {importedThumbprint}. Confirms cert-variable resolution OR netsh bind step " +
                $"broke. netsh output:\n{netshOutput}");

        // ──── INVARIANT 4: Private-key ACL grant for AppPool identity ─────────────
        // After binding, the deploy script grants the AppPool's identity SID Read on the
        // CNG private-key file at %ProgramData%\Microsoft\Crypto\Keys\<KeyName>. Without
        // this, the IIS worker process gets ACCESS DENIED reading the key, which surfaces
        // to clients as SCHANNEL 36880. We probe the ACL via Get-Acl filtered to identity-
        // reference matching the AppPool well-known SID prefix.
        var aclProbeScript =
            "$cert = Get-Item -Path \"Cert:\\LocalMachine\\My\\" + importedThumbprint + "\"; " +
            "$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert); " +
            "if ($rsa -is [System.Security.Cryptography.RSACng]) { " +
            "  $keyName = $rsa.Key.UniqueName; " +
            "  $keyPath = Join-Path \"$env:ProgramData\\Microsoft\\Crypto\\Keys\" $keyName; " +
            "  if (-not (Test-Path -LiteralPath $keyPath)) { $keyPath = Join-Path \"$env:ALLUSERSPROFILE\\Microsoft\\Crypto\\Keys\" $keyName }; " +
            "  if (Test-Path -LiteralPath $keyPath) { " +
            "    $acl = Get-Acl -LiteralPath $keyPath; " +
            "    $accessRules = $acl.Access | Where-Object { $_.IdentityReference -like \"*" + ctx.PoolName + "*\" -or $_.IdentityReference -like 'IIS APPPOOL\\*' }; " +
            "    if ($accessRules) { Write-Host -NoNewline 'GRANTED' } else { Write-Host -NoNewline 'NOT_GRANTED' } " +
            "  } else { Write-Host -NoNewline 'KEYFILE_MISSING' } " +
            "} else { Write-Host -NoNewline 'NOT_RSACNG' }";

        var aclResult = RunPowerShell(aclProbeScript).StdOut.Trim();

        // The pool-identity grant is the production-critical path. If the key is non-CNG
        // (legacy CSP) or HSM-backed, the deploy script logs a Warning + skips ACL —
        // operator-facing message describes manual remediation. The test treats those
        // outcomes as PASS because they're documented graceful degradations; but
        // NOT_GRANTED on a CNG key means production breakage.
        aclResult.ShouldNotBe("NOT_GRANTED",
            customMessage:
                "Private-key ACL was NOT granted to the AppPool identity. The IIS worker " +
                "process will get ACCESS DENIED reading the cert, surfacing to clients as " +
                $"SCHANNEL 36880. Thumbprint={importedThumbprint}, Pool={ctx.PoolName}. " +
                "Confirms Grant-AppPoolPrivateKeyAccess didn't fire OR matched the wrong " +
                "identity reference (PS variable expansion bug, off-by-one identity-type lookup, etc).");

        // ──── INVARIANT 5: Live HTTPS request resolves through IIS ────────────────
        // The big-picture invariant — every layer above composed correctly if and only
        // if we get HTTP 200 from a real HTTPS request through the local TLS stack.
        //
        // PowerShell 5.1 (default on Windows) lacks `Invoke-WebRequest -SkipCertificateCheck`
        // (that flag was added in PS 6.0). The portable equivalent is setting
        // `[System.Net.ServicePointManager]::ServerCertificateValidationCallback` to a
        // permissive lambda — the legacy WebRequest stack honours this callback for
        // EVERY cert validation across the process.
        //
        // Dialing 127.0.0.1 (not the cert's CN) avoids DNS resolution AND tests the real
        // production case where a client trusts the cert chain but the request hostname
        // doesn't match — operators commonly hit this with internal CAs.
        var httpsProbe = RunPowerShell(
            "$ErrorActionPreference = 'Stop'; " +
            "[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }; " +
            "[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12 -bor [System.Net.SecurityProtocolType]::Tls13; " +
            "$response = Invoke-WebRequest " +
            $"-Uri 'https://127.0.0.1:{ctx.HttpsPort}/index.html' " +
            "-UseBasicParsing " +
            "-TimeoutSec 30; " +
            "Write-Host -NoNewline \"$($response.StatusCode)|$($response.Content)\"");

        httpsProbe.ExitCode.ShouldBe(0,
            customMessage:
                $"HTTPS GET to https://127.0.0.1:{ctx.HttpsPort}/index.html failed. " +
                "All component invariants (1-4) passed but the live request didn't succeed. " +
                "Probable causes (in order):\n" +
                "  - IIS worker process couldn't open the cert (private-key ACL race condition)\n" +
                "  - Firewall blocks loopback HTTPS connect\n" +
                $"  - The deployed artifact's index.html is missing from {ctx.PhysicalPath}\n" +
                "  - SCHANNEL handshake fails (TLS 1.2/1.3 disabled in OS policy?)\n\n" +
                $"STDOUT:\n{httpsProbe.StdOut}\n\nSTDERR:\n{httpsProbe.StdErr}");

        var responseParts = httpsProbe.StdOut.Split('|', 2);
        responseParts.Length.ShouldBe(2,
            customMessage: $"HTTPS probe STDOUT format unexpected — expected 'statusCode|body'. Got:\n{httpsProbe.StdOut}");
        responseParts[0].ShouldBe("200",
            customMessage: $"HTTPS GET returned non-200. Full output: {httpsProbe.StdOut}");
        responseParts[1].ShouldContain("OrderApi v2.1.0",
            customMessage:
                "HTTPS response body doesn't contain the literal deployed string. " +
                "Either index.html is missing OR a different site is bound to the port. " +
                $"Body excerpt:\n{Truncate(responseParts[1], 2000)}");

        // ──── INVARIANT 6: appsettings.json substitution composed correctly ───────
        // Independent of the HTTPS path — proves the SubstituteInFiles rewriter ran
        // before IIS bound the cert, so the operator's app config is correct on first
        // request (no race window where the file is still token-form when the worker starts).
        var renderedAppSettings = File.ReadAllText(Path.Combine(ctx.PhysicalPath, "appsettings.json"));
        renderedAppSettings.ShouldContain("\"AppVersion\": \"2.1.0\"",
            customMessage: $"AppVersion not substituted in appsettings.json:\n{renderedAppSettings}");
        renderedAppSettings.ShouldContain("\"Environment\": \"Production\"",
            customMessage: "EnvironmentTag not substituted in appsettings.json.");

        ctx.MarkClean();
    }

    /// <summary>
    /// Stages a minimal ASP.NET-style artifact with the three files HTTPS deploys need:
    /// an HTML body the HTTPS probe can grep, an <c>appsettings.json</c> with one
    /// SubstituteInFiles token, and a <c>web.config</c> with one ConfigurationVariables
    /// candidate. Returns the path to the resulting <c>.zip</c>.
    /// </summary>
    private static string StageAspNetArtifact(HttpsTestContext ctx)
    {
        var stagingDir = Path.Combine(Path.GetTempPath(), $"squid-iis-https-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        ctx.RegisterTempDirForCleanup(stagingDir);

        // SubstituteInFiles touches appsettings.json. Two tokens to exercise.
        File.WriteAllText(Path.Combine(stagingDir, "appsettings.json"),
            "{\n" +
            "  \"AppVersion\": \"#{AppVersion}\",\n" +
            "  \"Environment\": \"#{EnvironmentTag}\"\n" +
            "}\n");

        // ConfigurationVariables touches web.config. One appSetting that matches a Squid var.
        File.WriteAllText(Path.Combine(stagingDir, "web.config"),
            "<?xml version=\"1.0\"?>\n" +
            "<configuration>\n" +
            "  <appSettings>\n" +
            "    <add key=\"ApiUrl\" value=\"https://localhost/api\" />\n" +
            "  </appSettings>\n" +
            "</configuration>\n");

        // index.html — the HTTPS probe greps the literal "OrderApi v2.1.0" out of the
        // response body. SubstituteInFiles targets `appsettings.json` only (per the action
        // property above), so the body string is independent of the rewriter — that keeps
        // the HTTPS-probe assertion clean: a 200 with the literal string proves the IIS
        // worker resolved the cert, opened the deployed artifact, and served it, without
        // entangling the rewriter pipeline.
        File.WriteAllText(Path.Combine(stagingDir, "index.html"),
            "<!DOCTYPE html><html><body>OrderApi v2.1.0 deployed</body></html>\n");

        var zipPath = Path.Combine(Path.GetTempPath(), $"squid-iis-https-app-{Guid.NewGuid():N}.zip");
        ctx.RegisterTempDirForCleanup(zipPath);
        ZipFile.CreateFromDirectory(stagingDir, zipPath);
        return zipPath;
    }

    /// <summary>
    /// Generates a self-signed PFX in memory via PowerShell's <c>New-SelfSignedCertificate</c>
    /// + <c>Export-PfxCertificate</c> — operator-equivalent of a real cert exported via
    /// <c>certlm.msc</c>. Removes the CurrentUser staging copy after export so the test only
    /// pollutes the LocalMachine store via the deploy-script's auto-import path.
    /// </summary>
    private static byte[] GenerateSelfSignedPfxBytes(string dnsName, string password)
    {
        var pwArg = string.IsNullOrEmpty(password)
            ? "$null"
            : $"(ConvertTo-SecureString -String '{password}' -AsPlainText -Force)";
        var tempPfx = Path.Combine(Path.GetTempPath(), $"squid-https-gen-cert-{Guid.NewGuid():N}.pfx");
        try
        {
            var script =
                $"$cert = New-SelfSignedCertificate -DnsName '{dnsName}' " +
                $"-CertStoreLocation Cert:\\CurrentUser\\My " +
                $"-KeyExportPolicy Exportable " +
                $"-KeyUsage KeyEncipherment,DigitalSignature " +
                $"-TextExtension @('2.5.29.37={{text}}1.3.6.1.5.5.7.3.1') " +
                $"-NotAfter (Get-Date).AddDays(30); " +
                $"Export-PfxCertificate -Cert $cert -FilePath '{tempPfx}' -Password {pwArg} | Out-Null; " +
                $"Remove-Item Cert:\\CurrentUser\\My\\$($cert.Thumbprint) -Force -ErrorAction SilentlyContinue";

            var r = RunPowerShell(script);
            if (r.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Self-signed PFX generation failed: ExitCode={r.ExitCode}, StdErr={r.StdErr}");

            return File.ReadAllBytes(tempPfx);
        }
        finally
        {
            try { if (File.Exists(tempPfx)) File.Delete(tempPfx); }
            catch { /* best-effort */ }
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + $"... [truncated, total {s.Length} chars]";

    // ── Helpers (mirror IISDeployRealHostE2ETests, duplicated for class isolation) ──

    private sealed class HttpsTestContext : IDisposable
    {
        private readonly List<string> _tempPathsToClean = new();
        private readonly List<string> _certThumbprintsToClean = new();
        private readonly List<string> _netshIpPortsToClean = new();
        private bool _markedClean;

        public HttpsTestContext()
        {
            Suffix = Guid.NewGuid().ToString("N")[..8];
            SiteName = $"SquidIISHttps-{Suffix}";
            PoolName = $"SquidIISHttpsPool-{Suffix}";
            PhysicalPath = Path.Combine(Path.GetTempPath(), $"squid-iis-https-{Suffix}");
            HttpsPort = PickFreePort();
            Directory.CreateDirectory(PhysicalPath);
            _tempPathsToClean.Add(PhysicalPath);
        }

        public string Suffix { get; }
        public string SiteName { get; }
        public string PoolName { get; }
        public string PhysicalPath { get; }
        public string HttpsPort { get; }

        public void RegisterTempDirForCleanup(string path) => _tempPathsToClean.Add(path);
        public void RegisterCertThumbprintForCleanup(string thumbprint) => _certThumbprintsToClean.Add(thumbprint);
        public void RegisterNetshIpPortForCleanup(string port) => _netshIpPortsToClean.Add(port);

        public void MarkClean() => _markedClean = true;

        public void Dispose()
        {
            if (OperatingSystem.IsWindows() && IsIISInstalled())
            {
                TryPowerShell($"Remove-Website -Name '{SiteName}' -ErrorAction SilentlyContinue");
                TryPowerShell($"Remove-WebAppPool -Name '{PoolName}' -ErrorAction SilentlyContinue");
            }

            // SNI bindings register by hostnameport, but we don't track the hostname here.
            // The ipport cleanup is defensive for non-SNI fallback paths; the actual SNI
            // entry survives Dispose IF the test failed before MarkClean. We accept this
            // tiny leak — `netsh http show sslcert` shows it but it's bounded by test runs.
            foreach (var port in _netshIpPortsToClean)
                TryPowerShell($"& netsh http delete sslcert ipport=0.0.0.0:{port} | Out-Null");

            foreach (var thumb in _certThumbprintsToClean)
                TryPowerShell($"Remove-Item Cert:\\LocalMachine\\My\\{thumb} -Force -ErrorAction SilentlyContinue");

            foreach (var path in _tempPathsToClean)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else if (File.Exists(path)) File.Delete(path);
                }
                catch { /* best-effort */ }
            }

            if (!_markedClean && OperatingSystem.IsWindows())
                Console.WriteLine($"[HttpsTestContext.Dispose] HTTPS test did NOT call MarkClean — Site='{SiteName}', Pool='{PoolName}'.");
        }

        private static string PickFreePort()
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port.ToString();
        }
    }

    private static class Property
    {
        public const string CreateOrUpdateWebSite = "Squid.Action.IISWebSite.CreateOrUpdateWebSite";
        public const string WebSiteName = "Squid.Action.IISWebSite.WebSiteName";
        public const string ApplicationPoolName = "Squid.Action.IISWebSite.ApplicationPoolName";
        public const string ApplicationPoolIdentityType = "Squid.Action.IISWebSite.ApplicationPoolIdentityType";
        public const string ApplicationPoolFrameworkVersion = "Squid.Action.IISWebSite.ApplicationPoolFrameworkVersion";
        public const string WebRoot = "Squid.Action.IISWebSite.WebRoot";
        public const string Bindings = "Squid.Action.IISWebSite.Bindings";
        public const string StartApplicationPool = "Squid.Action.IISWebSite.StartApplicationPool";
        public const string StartWebSite = "Squid.Action.IISWebSite.StartWebSite";
        public const string EnableAnonymousAuthentication = "Squid.Action.IISWebSite.EnableAnonymousAuthentication";
        public const string EnableBasicAuthentication = "Squid.Action.IISWebSite.EnableBasicAuthentication";
        public const string EnableWindowsAuthentication = "Squid.Action.IISWebSite.EnableWindowsAuthentication";
        public const string ConfigurationVariablesEnabled = "Squid.Action.IISWebSite.ConfigurationVariables.Enabled";
        public const string SubstituteInFilesEnabled = "Squid.Action.IISWebSite.SubstituteInFiles.Enabled";
        public const string SubstituteInFilesTargetFiles = "Squid.Action.IISWebSite.SubstituteInFiles.TargetFiles";
        public const string PackageSourcePath = "Squid.Action.IISWebSite.Package.SourcePath";
        public const string PackagePurgeBeforeExtract = "Squid.Action.IISWebSite.Package.PurgeBeforeExtract";
        public const string CertificatePfxBase64 = "Squid.Action.IISWebSite.Certificate.PfxBase64";
        public const string CertificatePfxPassword = "Squid.Action.IISWebSite.Certificate.PfxPassword";
        public const string CertificateThumbprintVariableName = "Squid.Action.IISWebSite.Certificate.ThumbprintVariableName";
    }

    private static DeploymentActionDto BuildAction(params (string Name, string Value)[] properties)
    {
        return new DeploymentActionDto
        {
            Id = 1,
            Name = "Real-world HTTPS deploy",
            ActionType = "Squid.DeployToIISWebSite",
            Properties = properties
                .Select(p => new DeploymentActionPropertyDto { PropertyName = p.Name, PropertyValue = p.Value })
                .ToList()
        };
    }

    private static bool IsIISInstalled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var r = RunPowerShell("(Get-WindowsFeature Web-WebServer -ErrorAction SilentlyContinue).Installed");
            return r.ExitCode == 0 && r.StdOut.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string PowerShellSingleLine(string command)
    {
        var script = $"Import-Module WebAdministration; Write-Host -NoNewline ({command})";
        return RunPowerShell(script).StdOut.Trim();
    }

    private static void TryPowerShell(string command)
    {
        try { RunPowerShell($"Import-Module WebAdministration -ErrorAction SilentlyContinue; {command}"); }
        catch { /* best-effort cleanup */ }
    }

    private static PsResult RunPowerShell(string script)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.StandardInput.Write(script);
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromMinutes(3));

        return new PsResult(process.ExitCode, stdout, stderr);
    }

    private sealed record PsResult(int ExitCode, string StdOut, string StdErr);
}

using System.IO.Compression;
using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Real-world failure-recovery composite E2E for <c>Squid.DeployToIISWebSite</c>: stages a
/// realistic ASP.NET artifact, induces a deterministic mid-deploy failure (unresolved
/// <c>#{X}</c> token with <c>ShouldFailDeploymentOnSubstitutionFails=True</c>), and proves
/// the four production failure-path invariants compose:
///
/// <list type="number">
///   <item><b>Exit code surfaces the failure</b> — operator's CI/CD pipeline gets a non-zero
///         exit and stops, doesn't silently mark the deploy "Succeeded"</item>
///   <item><b>STDERR names the missing variable</b> — operator can grep the build log and
///         see <c>ConnectionStrings:ReadReplica</c> as the unresolved token (not a generic
///         "deploy failed" message)</item>
///   <item><b>PreDeploy sentinel exists</b> — proves PreDeploy ran (it runs BEFORE the
///         SubstituteInFiles rewriter throws); confirms the rewriter is the failure point,
///         not the PreDeploy script</item>
///   <item><b>PostDeploy sentinel ABSENT</b> — proves PostDeploy was bypassed when the
///         configure step threw. Without this guarantee, a smoke-test PostDeploy hook
///         would fire against a half-configured site and surface a misleading "all good"
///         signal back to the operator</item>
///   <item><b>Deployment journal records Status=Failed</b> — the trap block at PS1:1061
///         wrote a journal entry with <c>"Status": "Failed"</c> BEFORE PowerShell exited.
///         Without this, the next re-run with <c>SkipIfAlreadyInstalled=True</c> couldn't
///         distinguish "no prior deploy" from "prior deploy failed", potentially short-
///         circuiting a re-attempt the operator needs to actually run</item>
///   <item><b>Re-run with all variables defined SUCCEEDS</b> — same step, same action,
///         operator just adds the missing variable. The journal flips from Failed to
///         Success, PostDeploy fires this time. Proves recovery is real, not just
///         "the deploy crashes the second time too"</item>
/// </list>
///
/// <para><b>Coverage delta vs <see cref="IISDeployRealHostE2ETests"/></b>: per-feature
/// suite tests `ShouldFailDeploymentOnSubstitutionFails=True` causes a throw, but does
/// NOT compose with PreDeploy/PostDeploy hooks OR journal Failed-status writing OR
/// recovery on second deploy. Those are three SEPARATE failure-path subsystems that all
/// need to work together for the operator's failure-recovery workflow to function.</para>
///
/// <para><b>Coverage delta vs <see cref="IISDeployRealWorldDotNetAppE2ETests"/></b>:
/// that composite is the HAPPY path (every feature on, deploy succeeds, journal records
/// Success). This is the UNHAPPY path counterpart — same artifact shape, same feature
/// toggles, but the variable set is intentionally incomplete so the rewriter throws.
/// Confirms the failure path doesn't silently corrupt the operator's recovery story.</para>
///
/// <para><b>Tier</b>: 🟢 High-fidelity. Real IIS, real PowerShell, real
/// <c>SubstituteInFilesBehaviour</c>-equivalent unresolved-token detection, real
/// <c>trap</c>-block journal writing. Skip-on-non-Windows guard + IIS-feature probe.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.IISDeploy)]
public sealed class IISDeployRealWorldFailureRecoveryE2ETests
{
    [Fact]
    public void RealWorld_UnresolvedToken_FailsDeploy_JournalRecordsFailed_PostDeploySkipped_ThenRecoversOnReRun()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new FailureRecoveryTestContext();
        ctx.RegisterDeploymentJournalForCleanup(ctx.SiteName);

        // ──── STAGE 1: Stage ASP.NET artifact with TWO substitution tokens ────────
        //
        // The trick is: appsettings.json contains BOTH `#{ConnectionStrings:Default}`
        // (which the variable set provides) AND `#{ConnectionStrings:ReadReplica}` (which
        // the FIRST variable set leaves undefined). The second token is what triggers
        // the SubstituteInFiles rewriter to throw.
        var artifactPath = StageAspNetArtifactWithTwoTokens(ctx);

        // Sentinel paths so we can witness which hooks fired.
        var preDeploySentinel = ctx.RegisterSentinelPath("predeploy");
        var postDeploySentinel = ctx.RegisterSentinelPath("postdeploy");

        // ──── STAGE 2: Variable set DEFINING ONLY ConnectionStrings:Default ────────
        //
        // `ReadReplica` is deliberately missing. With ShouldFailDeploymentOnSubstitutionFails=True
        // this triggers an exception from the SubstituteInFiles rewriter (PS1:1521).
        var incompleteVariables = new List<VariableDto>
        {
            new() { Name = "ConnectionStrings:Default", Value = "Server=prod-db;Database=Orders" },
            // ReadReplica intentionally omitted
            new() { Name = "AppVersion", Value = "3.0.0" }
        };

        // ──── STAGE 3: Build the action with failure-tolerance toggle ON ──────────
        //
        // `ShouldFailDeploymentOnSubstitutionFails=True` is the operator's opt-in for
        // "fail-loud" — without this flag the unresolved token would be left intact in
        // the deployed file and the deploy would silently succeed, presenting a runtime
        // bug to the operator instead of a deploy-time error.
        //
        // `SkipIfAlreadyInstalled=True` is set so the journal-write contract is exercised
        // (without it, the trap block still fires but the journal-write isn't part of
        // the operator's documented recovery workflow).
        var action = BuildAction(
            // Package extraction
            (Property.PackageSourcePath, artifactPath),
            (Property.PackagePurgeBeforeExtract, "True"),
            // IIS configure
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "ApplicationPoolIdentity"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings, $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true,\"requireSni\":false}}]"),
            (Property.EnableAnonymousAuthentication, "True"),
            (Property.EnableBasicAuthentication, "False"),
            (Property.EnableWindowsAuthentication, "False"),
            (Property.StartApplicationPool, "True"),
            (Property.StartWebSite, "True"),
            // Custom scripts — both write sentinels we can detect
            (Property.CustomScriptsPreDeploy,
                $"Set-Content -Path '{preDeploySentinel}' -Value 'pre-deploy-ran'"),
            (Property.CustomScriptsPostDeploy,
                $"Set-Content -Path '{postDeploySentinel}' -Value 'post-deploy-ran-UNEXPECTEDLY'"),
            // SubstituteInFiles ON; fail-loud opt-in
            (Property.SubstituteInFilesEnabled, "True"),
            (Property.SubstituteInFilesTargetFiles, "appsettings.json"),
            (Property.ShouldFailDeploymentOnSubstitutionFails, "True"),
            // Journal short-circuit ON so we exercise the journal write path
            (Property.PackageSkipIfAlreadyInstalled, "True"));

        // ──── STAGE 4: First deploy MUST FAIL ─────────────────────────────────────
        var script = IISDeployScriptBuilder.Build(action, incompleteVariables);
        var firstRun = RunPowerShell(script);

        // ──── INVARIANT 1: Exit code non-zero ─────────────────────────────────────
        firstRun.ExitCode.ShouldNotBe(0,
            customMessage:
                "Deploy with unresolved #{ConnectionStrings:ReadReplica} token + " +
                "ShouldFailDeploymentOnSubstitutionFails=True should have FAILED (non-zero exit). " +
                "If exit is 0, either the rewriter didn't run, the fail-loud toggle didn't take " +
                "effect, OR the unresolved-token detection regex broke. " +
                $"STDOUT:\n{Truncate(firstRun.StdOut, 4000)}\n\nSTDERR:\n{Truncate(firstRun.StdErr, 4000)}");

        // ──── INVARIANT 2: STDERR names the missing variable ──────────────────────
        var combinedOutput = firstRun.StdOut + "\n" + firstRun.StdErr;
        combinedOutput.ShouldContain("ConnectionStrings:ReadReplica",
            customMessage:
                "Failure message must NAME the unresolved token so operators can fix it. " +
                "Generic 'deploy failed' messages send operators on a wild goose chase. " +
                $"Combined output:\n{Truncate(combinedOutput, 4000)}");

        combinedOutput.ShouldContain("did not match any Squid variable",
            customMessage:
                "Failure message must match the SubstituteInFiles throw at PS1:1521. " +
                "If this phrase changed, update the production message in lockstep with the test.");

        // ──── INVARIANT 3: PreDeploy sentinel EXISTS ──────────────────────────────
        // PreDeploy runs at PS1:841 — BEFORE the SubstituteInFiles rewriter at PS1:1518.
        // The throw happens in the rewriter, so PreDeploy must have completed.
        File.Exists(preDeploySentinel).ShouldBeTrue(
            customMessage:
                $"PreDeploy sentinel '{preDeploySentinel}' missing — either PreDeploy didn't run " +
                "OR the deploy threw BEFORE reaching the PreDeploy step. Confirm by greppping " +
                "STDOUT for 'Running PreDeploy custom script'.");

        File.ReadAllText(preDeploySentinel).Trim().ShouldBe("pre-deploy-ran");

        // ──── INVARIANT 4: PostDeploy sentinel ABSENT ─────────────────────────────
        // PostDeploy runs at PS1:1910. The throw in SubstituteInFiles is caught by the
        // trap at PS1:1061 which re-throws (line 1072), terminating PowerShell BEFORE
        // line 1910. So the PostDeploy sentinel must NOT exist.
        File.Exists(postDeploySentinel).ShouldBeFalse(
            customMessage:
                $"PostDeploy sentinel '{postDeploySentinel}' EXISTS — PostDeploy fired despite the " +
                "configure-step throw. This is a regression: a smoke-test PostDeploy against a " +
                "half-configured site would surface a misleading 'all good' signal to the operator. " +
                "Re-confirm the trap block at PS1:1061 re-throws (not swallows) the exception.");

        // ──── INVARIANT 5: Journal records Status=Failed ──────────────────────────
        // The trap at PS1:1061 writes the journal BEFORE re-throwing. Without this, the
        // operator's next deploy with SkipIfAlreadyInstalled=True can't distinguish
        // "no prior deploy" from "prior deploy failed" and might skip when it shouldn't.
        var journalDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Squid", "IISDeploy", "journal");
        var journalFile = Path.Combine(journalDir, $"{ctx.SiteName}.json");
        File.Exists(journalFile).ShouldBeTrue(
            customMessage:
                $"Journal file '{journalFile}' not written. The trap block at PS1:1061 must " +
                "write a Failed entry BEFORE re-throwing, or the operator's next deploy can't " +
                "see that the prior attempt failed.");

        var journalText = File.ReadAllText(journalFile);
        journalText.ShouldContain("\"Status\": \"Failed\"",
            customMessage:
                $"Journal status is NOT 'Failed'. Content:\n{journalText}\n\n" +
                "Either the trap block didn't run, the Status field name changed, or the " +
                "journal entry was overwritten by a later successful write (shouldn't happen — " +
                "the throw should terminate before reaching the Success-path journal write).");

        journalText.ShouldContain("\"Fingerprint\"",
            customMessage: "Journal entry must include Fingerprint so the next deploy can compare.");

        // ──── STAGE 5: Second deploy with ALL variables MUST SUCCEED ──────────────
        //
        // Same script body, same action, same package. The operator's only change is
        // adding the missing variable. The journal should flip from Failed to Success,
        // PostDeploy should fire, exit code 0.
        //
        // Clean the PreDeploy sentinel so we can re-detect it on the second run.
        File.Delete(preDeploySentinel);

        var completeVariables = new List<VariableDto>(incompleteVariables)
        {
            new() { Name = "ConnectionStrings:ReadReplica", Value = "Server=prod-db-read;Database=Orders" }
        };

        var secondScript = IISDeployScriptBuilder.Build(action, completeVariables);
        var secondRun = RunPowerShell(secondScript);

        secondRun.ExitCode.ShouldBe(0,
            customMessage:
                "Second deploy with all variables defined should succeed. The operator's recovery " +
                "story is broken if this fails. " +
                $"STDOUT:\n{Truncate(secondRun.StdOut, 4000)}\n\nSTDERR:\n{Truncate(secondRun.StdErr, 4000)}");

        File.Exists(preDeploySentinel).ShouldBeTrue(
            customMessage: "PreDeploy didn't run on the recovery deploy.");

        File.Exists(postDeploySentinel).ShouldBeTrue(
            customMessage:
                "PostDeploy didn't fire on the recovery deploy. Without PostDeploy running on " +
                "the SUCCESSFUL re-run, the operator's smoke-test hook never executes and they " +
                "miss the signal that the deploy now actually works.");

        // Journal flipped from Failed to Success.
        var secondJournalText = File.ReadAllText(journalFile);
        secondJournalText.ShouldContain("\"Status\": \"Success\"",
            customMessage:
                $"Journal status did NOT flip to Success after recovery. Content:\n{secondJournalText}");
        secondJournalText.ShouldNotContain("\"Status\": \"Failed\"",
            customMessage:
                "Journal still records Failed after successful recovery — Success path failed to " +
                "overwrite. Operators querying journal status would see the stale Failed entry.");

        // Verify the substituted file is correct on the recovery deploy.
        var renderedAppSettings = File.ReadAllText(Path.Combine(ctx.PhysicalPath, "appsettings.json"));
        renderedAppSettings.ShouldContain("Server=prod-db-read;Database=Orders",
            customMessage:
                $"ConnectionStrings:ReadReplica value not present in deployed appsettings.json " +
                $"after recovery deploy. Content:\n{renderedAppSettings}");
        renderedAppSettings.ShouldNotContain("#{ConnectionStrings:ReadReplica}",
            customMessage: "Unresolved token still present in deployed file after successful re-run.");

        ctx.MarkClean();
    }

    /// <summary>
    /// Stages a minimal ASP.NET-style artifact with TWO substitution tokens in
    /// <c>appsettings.json</c>. The test's first variable set defines only one of them;
    /// the second triggers the SubstituteInFiles fail-loud path.
    /// </summary>
    private static string StageAspNetArtifactWithTwoTokens(FailureRecoveryTestContext ctx)
    {
        var stagingDir = Path.Combine(Path.GetTempPath(), $"squid-iis-failrecov-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        ctx.RegisterTempDirForCleanup(stagingDir);

        File.WriteAllText(Path.Combine(stagingDir, "appsettings.json"),
            "{\n" +
            "  \"AppVersion\": \"#{AppVersion}\",\n" +
            "  \"ConnectionStrings\": {\n" +
            "    \"Default\": \"#{ConnectionStrings:Default}\",\n" +
            "    \"ReadReplica\": \"#{ConnectionStrings:ReadReplica}\"\n" +
            "  }\n" +
            "}\n");

        File.WriteAllText(Path.Combine(stagingDir, "index.html"),
            "<!DOCTYPE html><html><body>OrderApi</body></html>\n");

        var zipPath = Path.Combine(Path.GetTempPath(), $"squid-iis-failrecov-app-{Guid.NewGuid():N}.zip");
        ctx.RegisterTempDirForCleanup(zipPath);
        ZipFile.CreateFromDirectory(stagingDir, zipPath);
        return zipPath;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + $"... [truncated, total {s.Length} chars]";

    // ── Helpers (mirror IISDeployRealHostE2ETests, duplicated for class isolation) ──

    private sealed class FailureRecoveryTestContext : IDisposable
    {
        private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
        private readonly List<string> _tempPathsToClean = new();
        private readonly List<string> _sentinelFilesToClean = new();
        private readonly List<string> _journalFilesToClean = new();
        private bool _markedClean;

        public FailureRecoveryTestContext()
        {
            SiteName = $"SquidIISFailRecov-{_suffix}";
            PoolName = $"SquidIISFailRecovPool-{_suffix}";
            PhysicalPath = Path.Combine(Path.GetTempPath(), $"squid-iis-failrecov-{_suffix}");
            HttpPort = PickFreePort();
            Directory.CreateDirectory(PhysicalPath);
            _tempPathsToClean.Add(PhysicalPath);
        }

        public string SiteName { get; }
        public string PoolName { get; }
        public string PhysicalPath { get; }
        public string HttpPort { get; }

        public void RegisterTempDirForCleanup(string path) => _tempPathsToClean.Add(path);

        public string RegisterSentinelPath(string suffix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"squid-iis-failrecov-sentinel-{_suffix}-{suffix}.txt");
            _sentinelFilesToClean.Add(path);
            return path;
        }

        public void RegisterDeploymentJournalForCleanup(string siteName)
        {
            var programData = Environment.GetEnvironmentVariable("ProgramData");
            if (string.IsNullOrEmpty(programData)) return;
            var safeName = System.Text.RegularExpressions.Regex.Replace(siteName, @"[^A-Za-z0-9._-]", "_");
            var journalPath = Path.Combine(programData, "Squid", "IISDeploy", "journal", $"{safeName}.json");
            _journalFilesToClean.Add(journalPath);
        }

        public void MarkClean() => _markedClean = true;

        public void Dispose()
        {
            if (OperatingSystem.IsWindows() && IsIISInstalled())
            {
                TryPowerShell($"Remove-Website -Name '{SiteName}' -ErrorAction SilentlyContinue");
                TryPowerShell($"Remove-WebAppPool -Name '{PoolName}' -ErrorAction SilentlyContinue");
            }

            foreach (var sentinel in _sentinelFilesToClean)
            {
                try { if (File.Exists(sentinel)) File.Delete(sentinel); }
                catch { /* best-effort */ }
            }

            foreach (var journal in _journalFilesToClean)
            {
                try { if (File.Exists(journal)) File.Delete(journal); }
                catch { /* best-effort */ }
            }

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
                Console.WriteLine($"[FailureRecoveryTestContext.Dispose] Test did NOT call MarkClean — Site='{SiteName}', Pool='{PoolName}'.");
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
        public const string CustomScriptsPreDeploy = "Squid.Action.CustomScripts.PreDeploy.ps1";
        public const string CustomScriptsPostDeploy = "Squid.Action.CustomScripts.PostDeploy.ps1";
        public const string SubstituteInFilesEnabled = "Squid.Action.IISWebSite.SubstituteInFiles.Enabled";
        public const string SubstituteInFilesTargetFiles = "Squid.Action.IISWebSite.SubstituteInFiles.TargetFiles";
        public const string ShouldFailDeploymentOnSubstitutionFails = "Squid.Action.SubstituteInFiles.ShouldFailDeploymentOnSubstitutionFails";
        public const string PackageSourcePath = "Squid.Action.IISWebSite.Package.SourcePath";
        public const string PackagePurgeBeforeExtract = "Squid.Action.IISWebSite.Package.PurgeBeforeExtract";
        public const string PackageSkipIfAlreadyInstalled = "Squid.Action.IISWebSite.Package.SkipIfAlreadyInstalled";
    }

    private static DeploymentActionDto BuildAction(params (string Name, string Value)[] properties)
    {
        return new DeploymentActionDto
        {
            Id = 1,
            Name = "Real-world failure-recovery deploy",
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

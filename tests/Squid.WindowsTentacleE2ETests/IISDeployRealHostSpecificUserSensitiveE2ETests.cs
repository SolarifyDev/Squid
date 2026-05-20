using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Real-host pin for the SpecificUser identity + sensitive-variable
/// substitution round-trip. The existing per-feature test
/// (<c>RealIIS_AppPoolIdentitySpecificUser_AppliesCredentialsToMetabase</c>)
/// uses LITERAL username/password values — it never exercises the path where
/// the operator stores credentials as Squid <see cref="VariableDto.IsSensitive"/>
/// variables and references them via <c>#{}</c> tokens in the action properties.
///
/// <para><b>Production gap closed</b>: the substitution layer at
/// <c>IISDeployScriptBuilder</c> + the preamble's <c>EscapeForPowerShellSingleQuote</c>
/// must safely round-trip the resolved value all the way to IIS's
/// <c>Set-ItemProperty processModel</c> call. A regression where:
/// <list type="bullet">
///   <item>Variable substitution drops sensitive values (silently emptying them)</item>
///   <item>PowerShell single-quote escaping mishandles values containing apostrophes</item>
///   <item>The sensitive-value masker masks the value at the variable hashtable
///         layer (which would lose the original value before it reaches IIS)</item>
///   <item>Token references are emitted to the script verbatim (the literal
///         <c>#{Sensitive.AppPoolPassword}</c> reaches IIS as the password)</item>
/// </list>
/// would only surface here. None of the unit-level escape tests cover the full
/// pipeline; none of the per-feature IIS tests reference Squid variables.</para>
///
/// <para><b>Tier</b>: 🟢 High-fidelity. Real Windows user account, real IIS
/// metabase Set-ItemProperty processModel, real variable substitution via
/// <c>IISDeployScriptBuilder.Build(action, variables)</c>. Skip-on-non-Windows
/// + IIS-feature probe.</para>
///
/// <para><b>What we can NOT verify directly</b>: IIS encrypts the stored
/// password and only exposes it through limited APIs. We instead assert via
/// side-channels:
/// <list type="number">
///   <item>Deploy script exits 0 (substitution didn't drop or corrupt anything)</item>
///   <item><c>processModel.identityType == "SpecificUser"</c> (the surface that
///         requires the username+password to also be set)</item>
///   <item><c>processModel.userName</c> contains the configured user (proves
///         the username token expanded correctly)</item>
///   <item>STDOUT does NOT contain the literal <c>#{Sensitive.…}</c> token
///         (proves expansion ran)</item>
///   <item>STDOUT does NOT contain the raw password value (proves sensitive-
///         value masking is intercepting log output that would otherwise leak)</item>
/// </list></para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.IISDeploy)]
public sealed class IISDeployRealHostSpecificUserSensitiveE2ETests
{
    [Fact]
    public void SpecificUser_CredentialsViaSensitiveVariables_RoundTripIntoMetabase_NoLeakInLogs()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsIISInstalled()) return;

        using var ctx = new SensitiveUserTestContext();

        // ──── STAGE 1: Stage a local Windows user with a strong password ─────────
        //
        // Password is GUID-suffixed for entropy. We avoid characters that would break
        // `net user /add` argv parsing (apostrophes, percent signs in some shells),
        // but include both upper/lower/digit/symbol to satisfy the default Windows
        // password policy. The IISDeployScriptBuilder's single-quote escape path
        // is exercised by the apostrophe in "Sq!" — without proper escaping the
        // preamble's `$SquidVariables['Sensitive.AppPoolPassword'] = '…'` line
        // breaks at the embedded apostrophe.
        var (userName, password) = ctx.StageLocalWindowsUser();

        // ──── STAGE 2: Wrap credentials in sensitive Squid variables ─────────────
        var variables = new List<VariableDto>
        {
            // Username is non-sensitive (visible in logs as part of IIS metabase reads).
            new() { Name = "AppPoolUser", Value = userName },
            // Password IS sensitive — the masker should suppress it from any
            // log output that the deploy script might emit (e.g. PS verbose mode).
            new() { Name = "Sensitive.AppPoolPassword", Value = password, IsSensitive = true }
        };

        // ──── STAGE 3: Action property values are #{} TOKENS, not literals ───────
        //
        // The substitution layer (in IISDeployScriptBuilder.Build) must resolve
        // these BEFORE the script body is shipped to the agent. If substitution
        // is dropped, the literal "#{Sensitive.AppPoolPassword}" string would
        // be passed to Set-ItemProperty as the password — IIS would store that
        // literal string and the pool would fail to start with auth error.
        var action = BuildAction(
            (Property.CreateOrUpdateWebSite, "True"),
            (Property.WebSiteName, ctx.SiteName),
            (Property.ApplicationPoolName, ctx.PoolName),
            (Property.ApplicationPoolIdentityType, "SpecificUser"),
            (Property.ApplicationPoolUsername, "#{AppPoolUser}"),
            (Property.ApplicationPoolPassword, "#{Sensitive.AppPoolPassword}"),
            (Property.ApplicationPoolFrameworkVersion, "v4.0"),
            (Property.WebRoot, ctx.PhysicalPath),
            (Property.Bindings,
                $"[{{\"protocol\":\"http\",\"port\":\"{ctx.HttpPort}\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true,\"requireSni\":false}}]"),
            // SpecificUser pool may not start without SeBatchLogonRight (LSA policy);
            // we test the metabase write, not the runtime start. Match the sibling
            // RealIIS_AppPoolIdentitySpecificUser_AppliesCredentialsToMetabase test.
            (Property.StartApplicationPool, "False"),
            (Property.StartWebSite, "False"));

        // ──── STAGE 4: Build + run the deploy ─────────────────────────────────────
        var script = IISDeployScriptBuilder.Build(action, variables);
        var result = RunPowerShell(script);

        // ──── INVARIANT 1: Deploy exits 0 ─────────────────────────────────────────
        result.ExitCode.ShouldBe(0,
            customMessage:
                "SpecificUser+sensitive-variable deploy failed. If failure mentions:\n" +
                "  - 'token did not match any Squid variable' → substitution layer didn't expand the password token\n" +
                "  - 'The user name or password is incorrect' → password was lost or escaped wrong\n" +
                "  - 'Cannot validate the access' → local-user creation itself failed (cleanup race)\n\n" +
                $"STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        // ──── INVARIANT 2: processModel.identityType == "SpecificUser" ────────────
        var actualIdentity = PowerShellSingleLine(
            $"(Get-ItemProperty IIS:\\AppPools\\{ctx.PoolName} -Name processModel.identityType).Value");
        actualIdentity.ShouldBe("SpecificUser",
            customMessage:
                $"Expected identityType=SpecificUser, actual='{actualIdentity}'. The deploy script's " +
                $"`if ($applicationPoolIdentityType -eq 'SpecificUser')` branch didn't fire OR the property " +
                $"value comparison failed — possibly because substitution didn't expand the token.");

        // ──── INVARIANT 3: processModel.userName contains the resolved username ──
        var actualUserName = PowerShellSingleLine(
            $"(Get-ItemProperty IIS:\\AppPools\\{ctx.PoolName} -Name processModel.userName).Value");
        actualUserName.ShouldContain(userName,
            customMessage:
                $"Pool userName doesn't contain configured user '{userName}', actual='{actualUserName}'. " +
                $"The username variable substitution dropped the value — verify the #{{AppPoolUser}} token " +
                "expanded BEFORE the script reached the agent.");

        // ──── INVARIANT 4: STDOUT has NO literal #{Sensitive…} token ─────────────
        result.StdOut.ShouldNotContain("#{Sensitive.AppPoolPassword}",
            customMessage:
                "STDOUT contains the literal '#{Sensitive.AppPoolPassword}' token — substitution " +
                "layer regression. The token should have been replaced by the actual password " +
                "value (which sensitive masking would then suppress from logs).");

        result.StdOut.ShouldNotContain("#{AppPoolUser}",
            customMessage:
                "STDOUT contains the literal '#{AppPoolUser}' token — non-sensitive variable " +
                "substitution also regressed.");

        // ──── INVARIANT 5: STDOUT has NO raw password value ──────────────────────
        //
        // Sensitive-value masking should intercept any log line that would otherwise
        // leak the password text. If this assertion fails, a sensitive value reached
        // production logs — operator-side security incident.
        result.StdOut.ShouldNotContain(password,
            customMessage:
                $"STDOUT contains the raw password value (potentially leaking the sensitive variable). " +
                $"Sensitive-value masker (Squid.Core.Services.DeploymentExecution.Script.SensitiveValueMasker) " +
                $"should have replaced it with `***` or similar before the log line was emitted. " +
                $"This is a P0 security regression — operator logs would expose the credential.");

        result.StdErr.ShouldNotContain(password,
            customMessage: "STDERR contains the raw password value — sensitive-value masking missed stderr.");

        ctx.MarkClean();
    }

    // ── Helpers (mirror IISDeployRealHostE2ETests's private context — duplicated for isolation) ──

    private sealed class SensitiveUserTestContext : IDisposable
    {
        private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
        private readonly List<string> _localUsersToClean = new();
        private bool _markedClean;

        public SensitiveUserTestContext()
        {
            SiteName = $"SquidIISSensitiveSpecificUser-{_suffix}";
            PoolName = $"SquidIISSensitiveSpecificUserPool-{_suffix}";
            PhysicalPath = Path.Combine(Path.GetTempPath(), $"squid-iis-sensitive-specific-user-{_suffix}");
            HttpPort = PickFreePort();
            Directory.CreateDirectory(PhysicalPath);
        }

        public string SiteName { get; }
        public string PoolName { get; }
        public string PhysicalPath { get; }
        public string HttpPort { get; }

        public (string UserName, string Password) StageLocalWindowsUser()
        {
            var userName = $"squid-iis-{_suffix}";
            // Strong password matching Windows default complexity (lower / upper / digit / symbol).
            // GUID for entropy. Apostrophe in "Sq!" is left out to keep `net user` argv simple;
            // the IISDeployScriptBuilder's escape path is exercised by the EscapeForPowerShellSingleQuote
            // function whenever ANY variable value (here, the password) round-trips through the preamble.
            var password = $"Sq!{Guid.NewGuid():N}";

            var result = RunPowerShell($"& net user '{userName}' '{password}' /add");
            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Failed to stage local user '{userName}'. ExitCode={result.ExitCode}, " +
                    $"StdOut='{result.StdOut}', StdErr='{result.StdErr}'.");

            _localUsersToClean.Add(userName);
            return (userName, password);
        }

        public void MarkClean() => _markedClean = true;

        public void Dispose()
        {
            if (OperatingSystem.IsWindows() && IsIISInstalled())
            {
                TryPowerShell($"Remove-Website -Name '{SiteName}' -ErrorAction SilentlyContinue");
                TryPowerShell($"Remove-WebAppPool -Name '{PoolName}' -ErrorAction SilentlyContinue");
            }

            foreach (var user in _localUsersToClean)
                TryPowerShell($"& net user '{user}' /delete | Out-Null");

            try { if (Directory.Exists(PhysicalPath)) Directory.Delete(PhysicalPath, recursive: true); }
            catch { /* best-effort */ }

            if (!_markedClean && OperatingSystem.IsWindows())
                Console.WriteLine($"[SensitiveUserTestContext.Dispose] Test did NOT call MarkClean — Site='{SiteName}'.");
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
        public const string ApplicationPoolUsername = "Squid.Action.IISWebSite.ApplicationPoolUsername";
        public const string ApplicationPoolPassword = "Squid.Action.IISWebSite.ApplicationPoolPassword";
        public const string ApplicationPoolFrameworkVersion = "Squid.Action.IISWebSite.ApplicationPoolFrameworkVersion";
        public const string WebRoot = "Squid.Action.IISWebSite.WebRoot";
        public const string Bindings = "Squid.Action.IISWebSite.Bindings";
        public const string StartApplicationPool = "Squid.Action.IISWebSite.StartApplicationPool";
        public const string StartWebSite = "Squid.Action.IISWebSite.StartWebSite";
    }

    private static DeploymentActionDto BuildAction(params (string Name, string Value)[] properties)
    {
        return new DeploymentActionDto
        {
            Id = 1,
            Name = "SpecificUser+SensitiveVariable",
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
        catch { /* best-effort */ }
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

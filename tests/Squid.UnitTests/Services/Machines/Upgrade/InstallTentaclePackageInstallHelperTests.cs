using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Behaviour of the root-owned package-install helper that
/// <c>deploy/scripts/install-tentacle.sh</c> writes and grants to the service
/// user through a bare-path sudoers rule. Because sudoers passes it ANY
/// arguments, this script is the whole least-privilege boundary for package
/// installs: it must accept exactly <c>(apt|dnf|yum, version)</c> and exec a
/// fixed argv.
///
/// <para>The real heredoc body is extracted and run with <c>/bin/sh</c>; the
/// only edit is pointing its single <c>SAFE_PATH=</c> line at a directory of
/// argv-recording stubs, so the package-manager argv the helper builds is
/// asserted exactly. Real-sudo coverage lives in
/// <c>SudoersCompatibilityE2ETests</c>.</para>
/// </summary>
public sealed class InstallTentaclePackageInstallHelperTests
{
    private const string ProductionSafePathLine = "SAFE_PATH=/usr/sbin:/usr/bin:/sbin:/bin";

    private const string AptUpdateArgv = "apt-get[update][-qq][-o][Dir::Etc::sourcelist=sources.list.d/squid.list][-o][Dir::Etc::sourceparts=-][-o][APT::Get::List-Cleanup=0][-o][Acquire::http::Timeout=60][-o][Acquire::Retries=1]";

    [Theory]
    [InlineData("apt", "1.6.0-sudoers", AptUpdateArgv + "|apt-get[install][-y][--allow-downgrades][-o][Acquire::http::Timeout=120][-o][Acquire::Retries=1][squid-tentacle=1.6.0-sudoers]")]
    [InlineData("apt", "1.9.9~rc.1+build.7", AptUpdateArgv + "|apt-get[install][-y][--allow-downgrades][-o][Acquire::http::Timeout=120][-o][Acquire::Retries=1][squid-tentacle=1.9.9~rc.1+build.7]")]
    [InlineData("dnf", "1.9.9-1", "dnf[install][-y][squid-tentacle-1.9.9-1]")]
    [InlineData("yum", "1.9.9-1", "yum[install][-y][squid-tentacle-1.9.9-1]")]
    public void Helper_ValidRequest_ExecsFixedPackageManagerArgv(string manager, string version, string expectedArgv)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var ctx = new HelperContext();

        var result = ctx.Run(manager, version);

        result.ExitCode.ShouldBe(0, $"helper must accept ({manager}, {version}). stderr:\n{result.Stderr}");
        ctx.RecordedArgv().ShouldBe(expectedArgv, "helper must exec exactly this argv — nothing from the caller beyond the validated version");
    }

    [Theory]
    [InlineData("1.0 /tmp/evil.deb")]           // second package / local .deb via word split
    [InlineData("1.0\n/tmp/evil.deb")]          // newline — a line-oriented grep check would pass this
    [InlineData("/tmp/evil.deb")]               // apt treats an argument with '/' ending .deb as a local file
    [InlineData("1.0/../evil.deb")]
    [InlineData("-o")]                          // package-manager option
    [InlineData("--allow-unauthenticated")]
    [InlineData("v1.0")]                        // must start with a digit
    [InlineData("")]
    [InlineData("1.0*")]                        // glob
    [InlineData("1.0=2")]
    [InlineData("1.0:2")]
    [InlineData("1.0;id")]
    [InlineData("1.0$(id)")]
    [InlineData("1.0é")]                        // non-ASCII must not slip through a locale-aware range
    [InlineData("1.0000000000000000000000000000000000000000000000000000000000000000")] // 65 chars
    public void Helper_RejectsVersion_WithoutRunningPackageManager(string version)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var ctx = new HelperContext();

        var result = ctx.Run("apt", version);

        result.ExitCode.ShouldBe(65, $"helper must reject version '{version}' with exit 65. stderr:\n{result.Stderr}");
        ctx.RecordedArgv().ShouldBeEmpty($"a rejected version '{version}' must never reach a package manager");
    }

    [Theory]
    [InlineData("apt")]
    [InlineData("apt|1.0|/tmp/evil.deb")]
    [InlineData("rpm|1.0")]
    [InlineData("apt-get|1.0")]
    [InlineData("")]
    public void Helper_RejectsUsage_WithoutRunningPackageManager(string pipeSeparatedArgs)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var args = pipeSeparatedArgs.Length == 0 ? Array.Empty<string>() : pipeSeparatedArgs.Split('|');

        using var ctx = new HelperContext();

        var result = ctx.Run(args);

        result.ExitCode.ShouldBe(64, $"helper must reject argv [{string.Join(", ", args)}] with exit 64. stderr:\n{result.Stderr}");
        ctx.RecordedArgv().ShouldBeEmpty("a usage error must never reach a package manager");
    }

    [Fact]
    public void Helper_RunsPackageManagerWithScrubbedEnvironment()
    {
        // sudo's env_reset is the first line of defence, but a host with
        // '!env_reset' (or a sudo implementation that keeps more) must still not
        // let the caller steer apt/dnf through APT_CONFIG and friends.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var ctx = new HelperContext();

        var result = ctx.Run(new[] { "dnf", "1.9.9-1" }, new Dictionary<string, string> { ["APT_CONFIG"] = "/tmp/evil.conf", ["LD_PRELOAD"] = "/tmp/evil.so" });

        result.ExitCode.ShouldBe(0, result.Stderr);
        ctx.RecordedEnvironment().ShouldBe("APT_CONFIG=<unset> LD_PRELOAD=<unset>", "the package manager must run under 'env -i' with only the fixed PATH");
    }

    [Fact]
    public void Helper_SafePathLine_AppearsExactlyOnce()
    {
        // The tests above rely on rewriting this single line; it is also the
        // only place the helper's PATH comes from.
        Regex.Matches(HelperContext.ExtractHelperBody(), Regex.Escape(ProductionSafePathLine)).Count.ShouldBe(1);
    }

    private sealed class HelperContext : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"squid-pkg-helper-{Guid.NewGuid():N}");
        private readonly string _argvLog;
        private readonly string _envLog;
        private readonly string _helperPath;

        public HelperContext()
        {
            Directory.CreateDirectory(_dir);
            _argvLog = Path.Combine(_dir, "argv.log");
            _envLog = Path.Combine(_dir, "env.log");

            foreach (var manager in new[] { "apt-get", "dnf", "yum" }) WriteStub(manager);

            // Stubs first, then the real system dirs: the helper keeps every
            // ordinary tool (env, grep, …) it would have in production, but
            // its package managers resolve to the recording stubs.
            _helperPath = Path.Combine(_dir, "package-install");
            File.WriteAllText(_helperPath, ExtractHelperBody().Replace(ProductionSafePathLine, $"SAFE_PATH={_dir}:/usr/bin:/bin", StringComparison.Ordinal));
        }

        /// <summary>
        /// Runs the helper under a UTF-8 caller locale: there, a shell bracket
        /// range like <c>[A-Za-z]</c> can match non-ASCII letters (macOS sh
        /// does), which only the helper's own <c>LC_ALL=C</c> prevents.
        /// </summary>
        public (int ExitCode, string Stderr) Run(params string[] args) => Run(args, new Dictionary<string, string> { ["LC_ALL"] = "en_US.UTF-8", ["LANG"] = "en_US.UTF-8" });

        public (int ExitCode, string Stderr) Run(string[] args, Dictionary<string, string> env)
        {
            var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(_helperPath);

            foreach (var a in args) psi.ArgumentList.Add(a);
            foreach (var (k, v) in env) psi.Environment[k] = v;

            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(TimeSpan.FromSeconds(10)).ShouldBeTrue("helper must finish within 10s");

            return (p.ExitCode, stderr.Result);
        }

        public string RecordedArgv() => File.Exists(_argvLog) ? string.Join("|", File.ReadAllLines(_argvLog)) : string.Empty;

        public string RecordedEnvironment() => File.Exists(_envLog) ? File.ReadAllText(_envLog).Trim() : "<no package manager ran>";

        private void WriteStub(string name)
        {
            var path = Path.Combine(_dir, name);

            File.WriteAllText(path, $"#!/bin/sh\nprintf '%s' '{name}' >> '{_argvLog}'\nfor a in \"$@\"; do printf '[%s]' \"$a\" >> '{_argvLog}'; done\necho >> '{_argvLog}'\necho \"APT_CONFIG=${{APT_CONFIG:-<unset>}} LD_PRELOAD=${{LD_PRELOAD:-<unset>}}\" > '{_envLog}'\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public static string ExtractHelperBody()
        {
            var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "deploy", "scripts", "install-tentacle.sh"));
            var match = Regex.Match(script, @"<<'HELPER_EOF'\n(.*?\n)HELPER_EOF\n", RegexOptions.Singleline);

            if (!match.Success)
                throw new InvalidOperationException("Could not locate the <<'HELPER_EOF' ... HELPER_EOF heredoc in install-tentacle.sh.");

            return match.Groups[1].Value;
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "deploy", "scripts"))) dir = dir.Parent;

            return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (deploy/scripts/) above the test assembly.");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch { /* best-effort */ }
        }
    }
}

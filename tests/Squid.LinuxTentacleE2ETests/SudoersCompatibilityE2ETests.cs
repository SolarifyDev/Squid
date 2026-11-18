using System.Diagnostics;
using Squid.Core.Services.Machines.Upgrade.Methods;
using Xunit.Abstractions;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Producer → consumer seam for the in-UI upgrade's sudo privileges, against
/// REAL sudo implementations. The producer is the sudoers + package-install
/// helper block of <c>deploy/scripts/install-tentacle.sh</c> (extracted
/// verbatim and run as root); the consumer is the bash the server actually
/// renders (<see cref="AptUpgradeMethod"/> / <see cref="YumUpgradeMethod"/>),
/// run as the service user through the container's own sudo.
///
/// <para>Each case is a throwaway container: classic sudo 1.8.31 / 1.9.5p2 /
/// 1.9.15p5 / 1.9.17p2 and sudo-rs 0.2.13 (Ubuntu 26.04 default) / 0.2.5
/// (Debian 13 opt-in). sudo-rs rejects any wildcard inside a command argument,
/// which is what made the pre-1.9.9 sudoers file fail <c>visudo</c> and left
/// new Ubuntu 26.04 installs with no upgrade privilege at all.</para>
///
/// <para>Package managers and systemd-run are root-owned argv-recording stubs
/// inside the container, so the assertions are on exactly what sudo let
/// through — no package repo is contacted.</para>
///
/// <para>Tier: 🟢 High (Rule 12.4) for the sudo boundary — real installer
/// bytes, real visudo, real sudo, real rendered snippets. The package-manager
/// execution itself is stubbed; the real dnf/apt run through the helper is
/// covered by <c>e2e-upgrade-matrix.yml</c>.</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.SudoersCompat)]
public sealed class SudoersCompatibilityE2ETests
{
    private const string TargetVersion = "1.9.9";

    private readonly ITestOutputHelper _testOutput;

    public SudoersCompatibilityE2ETests(ITestOutputHelper testOutput) { _testOutput = testOutput; }

    private const string ExpectedAptArgv =
        "apt-get[update][-qq][-o][Dir::Etc::sourcelist=sources.list.d/squid.list][-o][Dir::Etc::sourceparts=-][-o][APT::Get::List-Cleanup=0][-o][Acquire::http::Timeout=60][-o][Acquire::Retries=1]|" +
        "apt-get[install][-y][--allow-downgrades][-o][Acquire::http::Timeout=120][-o][Acquire::Retries=1][squid-tentacle=1.9.9]|";

    private const string ExpectedDnfArgv = "dnf[install][-y][squid-tentacle-1.9.9-1]|";

    private const string AptPrepare = "apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq sudo passwd >/dev/null";
    private const string DnfPrepare = "dnf install -y -q sudo util-linux shadow-utils";

    private sealed record SudoHost(string BaseImage, string Prepare, string Flavour, bool ClassicSudo, bool ScopeRuleMatches);

    private static readonly Dictionary<string, SudoHost> Hosts = new()
    {
        ["ubuntu-26.04-sudo-rs-0.2.13"] = new("ubuntu:26.04", AptPrepare, "default", ClassicSudo: false, ScopeRuleMatches: true),
        ["ubuntu-26.04-sudo.ws-1.9.17"] = new("ubuntu:26.04", AptPrepare, "ws", ClassicSudo: true, ScopeRuleMatches: true),
        ["ubuntu-24.04-sudo-1.9.15"] = new("ubuntu:24.04", AptPrepare, "default", ClassicSudo: true, ScopeRuleMatches: true),
        ["ubuntu-20.04-sudo-1.8.31"] = new("ubuntu:20.04", AptPrepare, "default", ClassicSudo: true, ScopeRuleMatches: true),
        ["rockylinux-8-sudo-1.9.5"] = new("rockylinux:8", DnfPrepare, "default", ClassicSudo: true, ScopeRuleMatches: true),
        ["rockylinux-9-sudo-1.9.17"] = new("rockylinux:9", DnfPrepare, "default", ClassicSudo: true, ScopeRuleMatches: true),
        // sudo-rs < 0.2.13 parses 'systemd-run … *' but reads the '*' literally,
        // so the scope rule never matches: unsupported for in-UI upgrade, and the
        // installer must say so. The helper rule (bare path) still matches.
        ["debian-13-sudo-rs-0.2.5"] = new("debian:13", AptPrepare + " && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq sudo-rs >/dev/null", "cargo", ClassicSudo: false, ScopeRuleMatches: false)
    };

    [Theory]
    [InlineData("ubuntu-26.04-sudo-rs-0.2.13")]
    [InlineData("ubuntu-26.04-sudo.ws-1.9.17")]
    [InlineData("ubuntu-24.04-sudo-1.9.15")]
    [InlineData("ubuntu-20.04-sudo-1.8.31")]
    [InlineData("rockylinux-8-sudo-1.9.5")]
    [InlineData("rockylinux-9-sudo-1.9.17")]
    [InlineData("debian-13-sudo-rs-0.2.5")]
    public void InstalledSudoers_ServerRenderedSnippets_GrantOnlySquidTentaclePackageInstall(string hostName)
    {
        if (!DockerAvailable()) return;

        var host = Hosts[hostName];

        using var ctx = new SeamContext(hostName);

        var (output, results) = ctx.Run(host);

        _testOutput.WriteLine(output);

        string R(string key) => results.TryGetValue(key, out var v) ? v : "<missing>";
        string Diag(string what) => $"[{hostName}] {what}. Rerun manually: see the container output below.\n\n{output}";

        R("installer_rc").ShouldBe("0", Diag("the installer's sudoers block must not abort the install"));
        R("installed_block").ShouldBe(host.ClassicSudo ? "core+legacy" : "core",
            Diag("classic sudo must get CORE+LEGACY (old servers keep their exact commands); sudo-rs must fall back to CORE alone rather than installing nothing"));
        R("helper_stat").ShouldBe("root:root:755", Diag("the package-install helper must be root-owned and not writable by the service user"));
        R("helper_dir_stat").ShouldBe("root:root:755", Diag("the helper's directory must be root-owned so the service user cannot swap the helper"));

        // New server: the helper path works on every sudo, with the exact argv.
        R("new_apt").ShouldBe("1", Diag("server-rendered apt snippet must install through the helper"));
        R("new_apt_argv").ShouldBe(ExpectedAptArgv, Diag("helper must run exactly the targeted apt-get update + pinned install"));
        R("new_yum").ShouldBe("1", Diag("server-rendered yum snippet must install through the helper"));
        R("new_yum_argv").ShouldBe(ExpectedDnfArgv, Diag("helper must run exactly 'dnf install -y squid-tentacle-V-R'"));

        // Old server (no helper call): classic sudo keeps the legacy rules, sudo-rs cannot.
        R("legacy_apt").ShouldBe(host.ClassicSudo ? "1" : "0", Diag("legacy apt commands must still match on classic sudo, and be denied (not hang) on sudo-rs"));
        R("legacy_apt_argv").ShouldBe(host.ClassicSudo ? ExpectedAptArgv : "", Diag("legacy apt argv"));
        R("legacy_yum").ShouldBe(host.ClassicSudo ? "1" : "0", Diag("legacy dnf command must still match on classic sudo"));
        R("legacy_yum_argv").ShouldBe(host.ClassicSudo ? ExpectedDnfArgv : "", Diag("legacy dnf argv"));

        // Least privilege: the helper refuses anything but (manager, version).
        foreach (var (probe, rc) in new[] { ("inject_space", "65"), ("inject_newline", "65"), ("inject_path", "65"), ("inject_option", "65"), ("extra_arg", "64"), ("unknown_mgr", "64") })
        {
            R(probe).ShouldBe(rc, Diag($"helper must reject '{probe}' with exit {rc}"));
            R($"{probe}_ran").ShouldBe("0", Diag($"'{probe}' must never reach a package manager"));
        }

        R("other_package").ShouldNotBe("0", Diag("the service user must not be able to apt-get install an arbitrary package"));
        R("other_package_ran").ShouldBe("0", Diag("arbitrary apt-get install must not run"));
        R("helper_tamper").ShouldNotBe("0", Diag("the service user must not be able to modify the helper"));

        R("scope").ShouldBe(host.ScopeRuleMatches ? "0" : "1", Diag("the systemd-run scope rule"));
        R("installer_warned_scope").ShouldBe(host.ScopeRuleMatches ? "0" : "1", Diag("the installer must warn exactly when sudo parses but never matches the scope rule"));
        R("installer_warned_helper").ShouldBe("0", Diag("the bare-path helper rule must match on every sudo"));

        // write_status(): status file lands without needing the legacy 'sudo mv' rule.
        R("status_written").ShouldBe("1", Diag("write_status must write last-upgrade.json as the service user (plain mv first)"));
        R("status_owner").ShouldBe("squid-tentacle", Diag("last-upgrade.json must stay owned by the service user"));

        ctx.MarkClean();
    }

    private static bool DockerAvailable()
    {
        var (exitCode, _) = Exec("docker", ["version", "--format", "{{.Server.Version}}"], TimeSpan.FromSeconds(30));

        if (exitCode == 0) return true;

        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            throw new InvalidOperationException("Docker is required for the sudoers seam test on CI but 'docker version' failed — check the runner image.");

        return false;
    }

    private static (int ExitCode, string Output) Exec(string file, IEnumerable<string> args, TimeSpan timeout, string stdin = null)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null, UseShellExecute = false };

        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi)!;

            if (stdin != null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }

            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return (-1, $"timed out after {timeout}: {file} {string.Join(' ', args)}");
            }

            return (p.ExitCode, stdout.Result + stderr.Result);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private sealed class SeamContext : IDisposable
    {
        private readonly string _hostName;
        private readonly string _workDir = Path.Combine(Path.GetTempPath(), $"squid-sudoers-seam-{Guid.NewGuid():N}");
        private readonly string _containerName = $"squid-sudoers-seam-{Guid.NewGuid():N}";
        private bool _clean;

        public SeamContext(string hostName) { _hostName = hostName; }

        public (string Output, Dictionary<string, string> Results) Run(SudoHost host)
        {
            var image = BuildImage(host);

            StageInputs();

            var (exitCode, output) = Exec("docker", ["run", "--rm", "--name", _containerName, "-v", $"{_workDir}:/seam-in:ro", image, "bash", "/seam-in/sudoers-seam.sh", host.Flavour], TimeSpan.FromMinutes(5));

            output.ShouldContain("SEAM_DONE", customMessage: $"[{_hostName}] seam script did not finish (docker exit {exitCode}). Output:\n{output}");

            var results = output.Split('\n').Where(l => l.StartsWith("RESULT ", StringComparison.Ordinal)).Select(l => l["RESULT ".Length..].TrimEnd('\r')).Select(l => l.Split('=', 2)).GroupBy(kv => kv[0]).ToDictionary(g => g.Key, g => g.Last()[1]);

            return (output, results);
        }

        private string BuildImage(SudoHost host)
        {
            var tag = $"squid-sudoers-seam:{_hostName}";
            var dockerfile = $"FROM {host.BaseImage}\nRUN {host.Prepare}\n";

            var (exitCode, output) = Exec("docker", ["build", "-q", "-t", tag, "-"], TimeSpan.FromMinutes(10), dockerfile);

            exitCode.ShouldBe(0, customMessage: $"[{_hostName}] could not build the sudo test image from {host.BaseImage} (needs network to the distro mirror). Reproduce: printf '{dockerfile.Replace("\n", "\\n")}' | docker build -t {tag} -\n{output}");

            return tag;
        }

        private void StageInputs()
        {
            Directory.CreateDirectory(_workDir);

            var repoRoot = LocateRepoRoot();

            File.Copy(Path.Combine(repoRoot, "deploy", "scripts", "install-tentacle.sh"), Path.Combine(_workDir, "install-tentacle.sh"));
            File.Copy(Path.Combine(repoRoot, "src", "Squid.Core", "Resources", "Upgrade", "upgrade-linux-tentacle.sh"), Path.Combine(_workDir, "upgrade-linux-tentacle.sh"));
            File.Copy(Path.Combine(repoRoot, "tests", "Squid.LinuxTentacleE2ETests", "Infrastructure", "sudoers-seam.sh"), Path.Combine(_workDir, "sudoers-seam.sh"));
            File.WriteAllText(Path.Combine(_workDir, "apt-snippet.sh"), new AptUpgradeMethod().RenderDetectAndInstall(TargetVersion));
            File.WriteAllText(Path.Combine(_workDir, "yum-snippet.sh"), new YumUpgradeMethod().RenderDetectAndInstall(TargetVersion));
        }

        private static string LocateRepoRoot()
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            while (dir != null && !File.Exists(Path.Combine(dir, "deploy", "scripts", "install-tentacle.sh"))) dir = Path.GetDirectoryName(dir);

            return dir ?? throw new FileNotFoundException("Could not locate the repo root (deploy/scripts/install-tentacle.sh) above the test assembly.");
        }

        public void MarkClean() => _clean = true;

        public void Dispose()
        {
            if (!_clean) Exec("docker", ["rm", "-f", _containerName], TimeSpan.FromSeconds(30));

            try { if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }
}

using Squid.Tentacle.Commands;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Instance;

namespace Squid.Tentacle.Tests.Commands;

/// <summary>
/// Defence-in-depth for the <c>run</c> path: if an operator's config (or the
/// shipping <c>appsettings.json</c>) leaves <c>CertsPath</c>/<c>WorkspacePath</c>
/// empty, <see cref="RunCommandPathResolver"/> must fill them in from the
/// per-platform instance paths rather than fall through to a Docker-era
/// hardcode like <c>/squid/certs</c> (which breaks every non-root systemd
/// install with UnauthorizedAccessException).
/// </summary>
public sealed class RunCommandPathResolverTests
{
    private static readonly InstanceRecord FakeInstance = new()
    {
        Name = "Default",
        ConfigPath = "/etc/squid-tentacle/instances/Default.config.json"
    };

    [Fact]
    public void EmptyCertsPath_ResolvedViaInstance()
    {
        var settings = new TentacleSettings { CertsPath = string.Empty };

        RunCommandPathResolver.FillMissingPaths(
            settings,
            FakeInstance,
            resolveCertsPath: _ => "/etc/squid-tentacle/instances/Default/certs");

        settings.CertsPath.ShouldBe("/etc/squid-tentacle/instances/Default/certs");
    }

    [Fact]
    public void WhitespaceCertsPath_ResolvedViaInstance()
    {
        // Config files occasionally end up with a whitespace-only value from
        // copy-paste; treat same as empty so the fallback still kicks in.
        var settings = new TentacleSettings { CertsPath = "   " };

        RunCommandPathResolver.FillMissingPaths(
            settings,
            FakeInstance,
            resolveCertsPath: _ => "/etc/squid-tentacle/instances/Default/certs");

        settings.CertsPath.ShouldBe("/etc/squid-tentacle/instances/Default/certs");
    }

    [Fact]
    public void ExplicitCertsPath_Preserved()
    {
        // Operator override from CLI / env / persisted config wins — we only
        // fill in when the value is absent, never overwrite an explicit choice.
        var settings = new TentacleSettings { CertsPath = "/custom/certs" };

        RunCommandPathResolver.FillMissingPaths(
            settings,
            FakeInstance,
            resolveCertsPath: _ => "/should/not/be/used");

        settings.CertsPath.ShouldBe("/custom/certs");
    }

    [Fact]
    public void EmptyWorkspacePath_ResolvedToSiblingOfInstanceDir()
    {
        // Workspace lives next to the instance's certs dir, under the instance
        // folder — keeps per-instance state self-contained so uninstall --purge
        // can rm -rf one tree.
        var settings = new TentacleSettings { WorkspacePath = string.Empty };

        RunCommandPathResolver.FillMissingPaths(
            settings,
            FakeInstance,
            resolveCertsPath: _ => "/etc/squid-tentacle/instances/Default/certs");

        settings.WorkspacePath.ShouldBe(Path.Combine("/etc/squid-tentacle/instances/Default", "work"));
    }

    [Fact]
    public void ExplicitWorkspacePath_Preserved()
    {
        var settings = new TentacleSettings { WorkspacePath = "/var/lib/squid/work" };

        RunCommandPathResolver.FillMissingPaths(
            settings,
            FakeInstance,
            resolveCertsPath: _ => "/ignored");

        settings.WorkspacePath.ShouldBe("/var/lib/squid/work");
    }

    [Fact]
    public void BothEmpty_BothFilledInOneCall()
    {
        var settings = new TentacleSettings
        {
            CertsPath = string.Empty,
            WorkspacePath = string.Empty
        };

        RunCommandPathResolver.FillMissingPaths(
            settings,
            FakeInstance,
            resolveCertsPath: _ => "/etc/squid-tentacle/instances/Default/certs");

        settings.CertsPath.ShouldBe("/etc/squid-tentacle/instances/Default/certs");
        settings.WorkspacePath.ShouldBe(Path.Combine("/etc/squid-tentacle/instances/Default", "work"));
    }
}

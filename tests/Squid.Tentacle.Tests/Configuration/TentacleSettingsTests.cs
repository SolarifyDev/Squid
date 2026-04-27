using Squid.Tentacle.Configuration;

namespace Squid.Tentacle.Tests.Configuration;

public class TentacleSettingsTests
{
    [Fact]
    public void PollingConnectionCount_Default_Is5()
    {
        var settings = new TentacleSettings();

        settings.PollingConnectionCount.ShouldBe(5);
    }

    [Fact]
    public void CertsPath_DefaultIsEmpty_SoRunCommandResolvesViaPlatformPaths()
    {
        // Regression: the old class default of "/squid/certs" was a root-only
        // path that caused UnauthorizedAccessException on every non-Docker
        // systemd install where `register` hadn't persisted a config yet.
        // Empty = "let RunCommand resolve via InstanceSelector", which gives
        // us /etc/squid-tentacle/instances/Default/certs (root) or
        // ~/.config/squid-tentacle/... (user).
        new TentacleSettings().CertsPath.ShouldBe(string.Empty);
    }

    [Fact]
    public void WorkspacePath_DefaultIsEmpty_SoRunCommandResolvesViaPlatformPaths()
    {
        // Same reasoning as CertsPath — /squid/work was a Docker-only convention
        // that silently traps native systemd installs behind a root-only path.
        new TentacleSettings().WorkspacePath.ShouldBe(string.Empty);
    }

    [Fact]
    public void GetServerCommsUrls_SingleUrl_ReturnsSingle()
    {
        var settings = new TentacleSettings { ServerCommsUrl = "https://server:10943" };

        var urls = settings.GetServerCommsUrls();

        urls.Count.ShouldBe(1);
        urls[0].ShouldBe("https://server:10943");
    }

    [Fact]
    public void GetServerCommsUrls_MultipleAddresses_ReturnsAll()
    {
        var settings = new TentacleSettings { ServerCommsAddresses = "https://a:10943,https://b:10943,https://c:10943" };

        var urls = settings.GetServerCommsUrls();

        urls.Count.ShouldBe(3);
        urls[0].ShouldBe("https://a:10943");
        urls[1].ShouldBe("https://b:10943");
        urls[2].ShouldBe("https://c:10943");
    }

    [Fact]
    public void GetServerCommsUrls_AddressesTakesPrecedence()
    {
        var settings = new TentacleSettings
        {
            ServerCommsUrl = "https://single:10943",
            ServerCommsAddresses = "https://ha1:10943,https://ha2:10943"
        };

        var urls = settings.GetServerCommsUrls();

        urls.Count.ShouldBe(2);
        urls[0].ShouldBe("https://ha1:10943");
    }

    [Fact]
    public void GetServerCommsUrls_Empty_ReturnsEmpty()
    {
        var settings = new TentacleSettings();

        var urls = settings.GetServerCommsUrls();

        urls.ShouldBeEmpty();
    }

    [Fact]
    public void GetServerCommsUrls_TrimWhitespace()
    {
        var settings = new TentacleSettings { ServerCommsAddresses = " https://a:10943 , https://b:10943 " };

        var urls = settings.GetServerCommsUrls();

        urls.Count.ShouldBe(2);
        urls[0].ShouldBe("https://a:10943");
        urls[1].ShouldBe("https://b:10943");
    }

    [Fact]
    public void GetServerCommsUrls_EmptyEntries_Skipped()
    {
        var settings = new TentacleSettings { ServerCommsAddresses = "https://a:10943,,https://b:10943," };

        var urls = settings.GetServerCommsUrls();

        urls.Count.ShouldBe(2);
    }

    // ── P1-Phase9.5 drain timeout default ────────────────────────────────────

    [Fact]
    public void ShutdownDrainTimeoutSeconds_Default_Is300_For30MinDeploys()
    {
        // Pre-Phase-9.5 default was 30s — would kill a 10-minute deploy at
        // SIGTERM. New default is 300s (5 min), covers the median Linux
        // tentacle deploy window. Operators with longer deploys override via
        // Tentacle:ShutdownDrainTimeoutSeconds in appsettings.
        new TentacleSettings().ShutdownDrainTimeoutSeconds.ShouldBe(300);
    }

    [Fact]
    public void DefaultShutdownDrainTimeoutSeconds_ConstantValuePinned()
    {
        // Rule 8 — pin the literal so a "harmless" cleanup rename can't
        // silently regress the Phase-9.5 default. Operators may have set
        // systemd TimeoutStopSec to match this value; a regression would
        // mismatch the unit's grace window.
        TentacleSettings.DefaultShutdownDrainTimeoutSeconds.ShouldBe(300);
    }
}

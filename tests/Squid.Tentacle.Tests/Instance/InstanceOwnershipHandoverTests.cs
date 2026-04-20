using Squid.Tentacle.Instance;

namespace Squid.Tentacle.Tests.Instance;

/// <summary>
/// Covers the "after <c>sudo register</c>, hand ownership of the persisted
/// config file + certs dir to the systemd service user" step. Without this
/// the service user (<c>squid-tentacle</c>) hits <c>UnauthorizedAccessException</c>
/// on startup because root-owned 0600 files are unreadable by anyone else.
/// </summary>
public sealed class InstanceOwnershipHandoverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;
    private readonly string _instanceDir;
    private readonly string _certsDir;
    private readonly InstanceRecord _instance;

    public InstanceOwnershipHandoverTests()
    {
        // Build a realistic instance layout under a temp root — config.json alongside
        // an instance folder that holds the certs subdir. Mirrors what
        // PlatformPaths.GetInstanceConfigPath / GetInstanceCertsDir produce.
        _tempDir = Path.Combine(Path.GetTempPath(), $"squid-handover-{Guid.NewGuid():N}");
        var instancesRoot = Path.Combine(_tempDir, "instances");
        Directory.CreateDirectory(instancesRoot);

        _configPath = Path.Combine(instancesRoot, "Default.config.json");
        File.WriteAllText(_configPath, "{}");

        _instanceDir = Path.Combine(instancesRoot, "Default");
        _certsDir = Path.Combine(_instanceDir, "certs");
        Directory.CreateDirectory(_certsDir);

        _instance = new InstanceRecord
        {
            Name = "Default",
            ConfigPath = _configPath
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void HandOver_NotRoot_Skipped()
    {
        // Regular-user register (no sudo) already writes files the user owns —
        // nothing to hand over. Don't shell out to chown pointlessly.
        var calls = new List<(string Path, string User)>();
        var handover = new InstanceOwnershipHandover(
            isRoot: () => false,
            detectServiceUser: () => "squid-tentacle",
            chown: (p, u) => { calls.Add((p, u)); return true; });

        var result = handover.HandOver(_instance, _instanceDir);

        result.DidHandOver.ShouldBeFalse();
        result.Reason.ShouldContain("not running as root");
        calls.ShouldBeEmpty();
    }

    [Fact]
    public void HandOver_RootButNoServiceUser_Skipped()
    {
        // Dev / Docker / air-gap install where install-tentacle.sh wasn't the
        // source of the user account. We can't guess who "should" own the
        // files — skip rather than chown to a user that may not be intended.
        var calls = new List<(string, string)>();
        var handover = new InstanceOwnershipHandover(
            isRoot: () => true,
            detectServiceUser: () => null,
            chown: (p, u) => { calls.Add((p, u)); return true; });

        var result = handover.HandOver(_instance, _instanceDir);

        result.DidHandOver.ShouldBeFalse();
        result.Reason.ShouldContain("service user");
        calls.ShouldBeEmpty();
    }

    [Fact]
    public void HandOver_RootWithServiceUser_ChownsConfigAndInstanceDir()
    {
        // Core scenario: `sudo squid-tentacle register ...` just finished.
        // config file + instance dir must both be handed to squid-tentacle,
        // otherwise systemd crashes on UnauthorizedAccessException.
        var calls = new List<(string Path, string User)>();
        var handover = new InstanceOwnershipHandover(
            isRoot: () => true,
            detectServiceUser: () => "squid-tentacle",
            chown: (p, u) => { calls.Add((p, u)); return true; });

        var result = handover.HandOver(_instance, _instanceDir);

        result.DidHandOver.ShouldBeTrue();
        result.ServiceUser.ShouldBe("squid-tentacle");
        result.Paths.ShouldContain(_configPath);
        result.Paths.ShouldContain(_instanceDir);

        calls.ShouldContain(c => c.Path == _configPath && c.User == "squid-tentacle");
        calls.ShouldContain(c => c.Path == _instanceDir && c.User == "squid-tentacle");
    }

    [Fact]
    public void HandOver_MissingConfigFile_StillChownsInstanceDir()
    {
        // Defensive: if somehow the config wasn't written (early failure in
        // register) but the certs dir was created, still hand over what does
        // exist — skipping silently leaves the dir root-owned which a retry
        // of `register` would then fail on.
        File.Delete(_configPath);

        var calls = new List<(string Path, string User)>();
        var handover = new InstanceOwnershipHandover(
            isRoot: () => true,
            detectServiceUser: () => "squid-tentacle",
            chown: (p, u) => { calls.Add((p, u)); return true; });

        var result = handover.HandOver(_instance, _instanceDir);

        result.DidHandOver.ShouldBeTrue();
        result.Paths.ShouldNotContain(_configPath);
        result.Paths.ShouldContain(_instanceDir);
    }

    [Fact]
    public void HandOver_ChownFails_ReturnsFalse_ButAttemptedAll()
    {
        // If chown itself errors (exotic FS, weird perms), we still try every
        // path — one chown failure mustn't abort the others, because a partial
        // handover is better than none (the working one gets the service user
        // through startup; the broken one surfaces in logs).
        var attempted = new List<string>();
        var handover = new InstanceOwnershipHandover(
            isRoot: () => true,
            detectServiceUser: () => "squid-tentacle",
            chown: (p, u) => { attempted.Add(p); return false; });

        var result = handover.HandOver(_instance, _instanceDir);

        result.DidHandOver.ShouldBeFalse();
        attempted.ShouldContain(_configPath);
        attempted.ShouldContain(_instanceDir);
    }

    [Fact]
    public void HandOver_CertsDirPathNullOrEmpty_Safe()
    {
        // Extra defense — if the caller passes an empty instanceDir (shouldn't
        // happen in practice because Path.GetDirectoryName always returns
        // something for our paths), we don't NPE, we just skip that slot.
        var calls = new List<(string Path, string User)>();
        var handover = new InstanceOwnershipHandover(
            isRoot: () => true,
            detectServiceUser: () => "squid-tentacle",
            chown: (p, u) => { calls.Add((p, u)); return true; });

        var result = handover.HandOver(_instance, instanceDir: null);

        // Config still handed over even though instance dir was missing.
        result.Paths.ShouldContain(_configPath);
        result.Paths.ShouldNotContain(_instanceDir);
        calls.ShouldHaveSingleItem();
    }
}

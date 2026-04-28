using System.Diagnostics;
using Serilog;

namespace Squid.Tentacle.Instance;

/// <summary>
/// After <c>sudo squid-tentacle register</c> persists config and certs, transfers
/// ownership of those files to the systemd service user (conventionally
/// <c>squid-tentacle</c>, created by <c>install-tentacle.sh</c>).
/// </summary>
/// <remarks>
/// <para><b>Why this exists:</b> <see cref="Configuration.TentacleConfigFile.Save"/>
/// writes the persisted instance config via
/// <see cref="Platform.AtomicFileWriter.WriteAllTextRestricted"/>, which sets
/// Unix mode <c>0600</c> to protect the API key / server thumbprint at rest.
/// When <c>register</c> runs under <c>sudo</c>, files are created as
/// <c>root:root 0600</c>. The systemd unit installed by <c>service install</c>
/// runs as <c>User=squid-tentacle</c> — which can neither read nor write
/// root-owned 0600 files. Result: <c>UnauthorizedAccessException</c> on every
/// service start, crash loop forever.</para>
///
/// <para><b>The fix:</b> at the end of a successful <c>register</c> run, if we
/// are running as root AND the service user exists on this host, <c>chown -R</c>
/// the instance's config file + instance directory (contains certs) to the
/// service user. Idempotent — re-running after a previous handover is a no-op
/// (chown on already-correct ownership is fine).</para>
///
/// <para><b>When it's a no-op:</b>
/// <list type="bullet">
/// <item>Not running as root (regular-user <c>register</c> writes to
/// <c>~/.config/...</c>, already self-owned).</item>
/// <item>Service user doesn't exist (Docker / dev / custom install where the
/// operator manages identity themselves — we can't guess who to hand to).</item>
/// <item>Non-Linux platform (Windows / macOS use different service-user
/// conventions and this class only knows the Linux story).</item>
/// </list></para>
/// </remarks>
public sealed class InstanceOwnershipHandover
{
    /// <summary>Dedicated service user, created by <c>install-tentacle.sh</c>.</summary>
    public const string DefaultServiceUser = "squid-tentacle";

    private readonly Func<bool> _isRoot;
    private readonly Func<string> _detectServiceUser;
    private readonly Func<string, string, bool> _chown;

    /// <summary>Production default — delegates to the platform-resolved
    /// <see cref="Platform.IServiceUserProvider"/> (Phase-12.A.3) so the
    /// same call site works on Linux + Windows + macOS without OS branches.</summary>
    public InstanceOwnershipHandover()
        : this(Platform.ServiceUserProviderFactory.Resolve())
    {
    }

    /// <summary>
    /// P1-Phase12.A.3 — provider-based ctor. Internally adapts to the
    /// existing 3-Func seam so the test ctor (and all 10+ existing tests
    /// that use it) continues to work without modification.
    /// </summary>
    public InstanceOwnershipHandover(Platform.IServiceUserProvider serviceUserProvider)
        : this(
            isRoot: serviceUserProvider.IsRunningElevated,
            detectServiceUser: () =>
            {
                var u = serviceUserProvider.DefaultServiceUser;
                return string.IsNullOrEmpty(u) || !serviceUserProvider.ServiceUserExists(u) ? null : u;
            },
            chown: serviceUserProvider.TrySetOwnership)
    {
    }

    /// <summary>Test constructor — every OS/process boundary is injectable so the unit tests can run anywhere.</summary>
    internal InstanceOwnershipHandover(Func<bool> isRoot, Func<string> detectServiceUser, Func<string, string, bool> chown)
    {
        _isRoot = isRoot ?? throw new ArgumentNullException(nameof(isRoot));
        _detectServiceUser = detectServiceUser ?? throw new ArgumentNullException(nameof(detectServiceUser));
        _chown = chown ?? throw new ArgumentNullException(nameof(chown));
    }

    /// <summary>
    /// Hands <paramref name="instance"/>'s config file and
    /// <paramref name="instanceDir"/> (parent of the certs dir) to the service
    /// user. Caller resolves the instance dir — typically
    /// <c>Path.GetDirectoryName(InstanceSelector.ResolveCertsPath(instance))</c>.
    /// </summary>
    public HandoverResult HandOver(InstanceRecord instance, string instanceDir)
    {
        ArgumentNullException.ThrowIfNull(instance);

        // Single gate covering both "not Linux" and "not root" — the default
        // isRoot implementation folds the Linux check in, so production on
        // Windows/macOS always short-circuits here regardless of user. Tests
        // inject their own isRoot to exercise the downstream branches.
        if (!_isRoot())
            return HandoverResult.Skipped("not running as root — files are already owned by the invoking user");

        var user = _detectServiceUser();

        if (string.IsNullOrEmpty(user))
            return HandoverResult.Skipped($"service user '{DefaultServiceUser}' not found on this host — can't infer ownership target");

        // Walk every artifact register may have created. Each slot fails open
        // (we attempt all of them even if one chown fails) because a partial
        // handover is strictly better than none — the working part lets the
        // service read SOMETHING and fail with a more specific error than
        // PermissionDenied, which is far easier to diagnose.
        var attempts = new List<(string Path, bool Ok, bool Existed)>
        {
            AttemptChown(instance.ConfigPath, user),
            AttemptChown(instanceDir, user)
        };

        var handed = attempts.Where(a => a.Existed && a.Ok).Select(a => a.Path).ToList();
        var allSucceeded = attempts.All(a => !a.Existed || a.Ok);
        var anyHanded = handed.Count > 0;

        return new HandoverResult(
            DidHandOver: anyHanded && allSucceeded,
            Reason: anyHanded && allSucceeded
                ? "handed over"
                : "one or more chown operations failed — see warning logs",
            ServiceUser: user,
            Paths: handed);
    }

    private (string Path, bool Ok, bool Existed) AttemptChown(string path, string user)
    {
        if (string.IsNullOrWhiteSpace(path)) return (path, Ok: false, Existed: false);

        var existed = File.Exists(path) || Directory.Exists(path);

        if (!existed) return (path, Ok: false, Existed: false);

        var ok = _chown(path, user);

        if (!ok) Log.Warning("[Handover] chown {Path} → {User} failed; systemd may still hit PermissionDenied on startup", path, user);

        return (path, ok, Existed: true);
    }

    // ── Production implementations of the injected seams ────────────────────

    private static bool DefaultIsRoot()
    {
        // Fold the Linux platform check in here so production runs on
        // Windows/macOS short-circuit HandOver at the root gate. The separate
        // "not Linux" message doesn't add diagnostic value — if an operator
        // ran `sudo register` on Windows they've already wandered so far off
        // the supported path that a generic "not root" message is fine.
        if (!OperatingSystem.IsLinux()) return false;

        return Environment.UserName.Equals("root", StringComparison.Ordinal);
    }

    private static string DefaultDetectServiceUser()
    {
        try
        {
            // `getent passwd USER` exits 0 when the user is defined in any NSS
            // source (files, ldap, sssd), non-zero otherwise. Matches the check
            // ServiceCommand uses to decide whether to run systemd as that user.
            var psi = new ProcessStartInfo("getent", $"passwd {DefaultServiceUser}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var proc = Process.Start(psi);

            if (proc == null) return null;

            proc.WaitForExit(TimeSpan.FromSeconds(5));

            return proc.ExitCode == 0 ? DefaultServiceUser : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool DefaultChownRecursive(string path, string user)
    {
        try
        {
            // .NET 9 has no managed chown primitive — shelling out to the
            // system `chown` is the portable Unix answer and matches what
            // install-tentacle.sh already does for the install dir.
            var psi = new ProcessStartInfo("chown", $"-R {user}:{user} \"{path}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var proc = Process.Start(psi);

            if (proc == null) return false;

            proc.WaitForExit(TimeSpan.FromSeconds(10));

            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Outcome of an <see cref="InstanceOwnershipHandover.HandOver"/> call.
/// <see cref="DidHandOver"/> is only true when we actually chowned ≥ 1 path
/// and every attempted chown succeeded.
/// </summary>
public sealed record HandoverResult(bool DidHandOver, string Reason, string ServiceUser, IReadOnlyList<string> Paths)
{
    public static HandoverResult Skipped(string reason)
        => new(DidHandOver: false, Reason: reason, ServiceUser: null, Paths: Array.Empty<string>());
}

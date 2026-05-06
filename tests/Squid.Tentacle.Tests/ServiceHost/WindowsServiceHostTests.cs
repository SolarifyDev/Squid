using Shouldly;
using Squid.Tentacle.ServiceHost;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ServiceHost;

/// <summary>
/// pin the <c>sc.exe</c> argv contract for Windows Service
/// install/uninstall/start/stop/status.
///
/// <para><b>sc.exe argv shape (CORRECTED)</b>: each option is delivered as
/// <b>TWO separate argv tokens</b>: a key with trailing <c>=</c> (e.g.
/// <c>"binPath="</c>) followed by the value (e.g. <c>"\"C:\\path\\to.exe\" run"</c>).
/// The Microsoft docs example <c>sc.exe create NewService binPath= "..."</c>
/// is a COMMAND-LINE form — when parsed by Windows CommandLineToArgvW the
/// key and value become two separate argvs. Earlier versions of this builder
/// packed both into a single argv (<c>"binPath= ..."</c>) which .NET then
/// quoted-as-one-token, causing sc.exe to read the WHOLE thing as the binPath
/// value and bail on the next option with <c>ERROR: Invalid type= field</c>.
/// The bug was caught the first time the production argv shape ran against
/// real <c>sc.exe</c> on a windows-latest runner via
/// <c>WindowsServiceFixture</c> in the upgrade-pipeline E2E suite.</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class WindowsServiceHostTests
{
    // ── BuildScCreateArgs — the most complex argv builder ──────────────────────

    [Fact]
    public void BuildScCreateArgs_SimpleRequest_ProducesPinnedArgv()
    {
        var args = WindowsServiceHost.BuildScCreateArgs(new ServiceInstallRequest
        {
            ServiceName = "squid-tentacle",
            Description = "Squid Tentacle Agent (Default)",
            ExecStart = @"C:\Program Files\Squid Tentacle\Squid.Tentacle.exe",
            ExecArgs = new[] { "run" },
            WorkingDirectory = @"C:\Program Files\Squid Tentacle"
        });

        // 10 elements = 2 verb/name + 4 key/value pairs.
        args.Length.ShouldBe(10);
        args[0].ShouldBe("create");
        args[1].ShouldBe("squid-tentacle");

        // binPath= and its value are TWO separate argvs.
        // The value: exe path wrapped in escaped double quotes (defends
        // against spaces in install path: "C:\Program Files\…") + args
        // appended after the closing quote, space-separated.
        args[2].ShouldBe("binPath=");
        args[3].ShouldBe(@"""C:\Program Files\Squid Tentacle\Squid.Tentacle.exe"" run");

        args[4].ShouldBe("DisplayName=");
        args[5].ShouldBe("Squid Tentacle Agent (Default)");

        // start= auto — SCM auto-starts the service at boot. Dropping this
        // would leave the service installed but not running on reboot.
        args[6].ShouldBe("start=");
        args[7].ShouldBe("auto");

        // obj= LocalSystem is the explicit equivalent of sc.exe's default;
        // pinning it here makes the contract visible in audit logs.
        args[8].ShouldBe("obj=");
        args[9].ShouldBe("LocalSystem");
    }

    [Fact]
    public void BuildScCreateArgs_NoExecArgs_OmitsTrailingSpace()
    {
        // ExecArgs is empty → binPath value is just the quoted exe with no
        // trailing whitespace. Defends against an empty trailing arg that
        // would confuse sc.exe's parser.
        var args = WindowsServiceHost.BuildScCreateArgs(new ServiceInstallRequest
        {
            ServiceName = "squid-tentacle",
            Description = "Squid Tentacle Agent",
            ExecStart = @"C:\Program Files\Squid\foo.exe",
            ExecArgs = Array.Empty<string>()
        });

        args[2].ShouldBe("binPath=");
        args[3].ShouldBe(@"""C:\Program Files\Squid\foo.exe""");
    }

    [Fact]
    public void BuildScCreateArgs_NullExecArgs_TreatedAsEmpty()
    {
        // Defensive — caller may pass null when the service has no args.
        var args = WindowsServiceHost.BuildScCreateArgs(new ServiceInstallRequest
        {
            ServiceName = "squid-tentacle",
            Description = "Squid Tentacle Agent",
            ExecStart = @"C:\foo.exe",
            ExecArgs = null!
        });

        args[2].ShouldBe("binPath=");
        args[3].ShouldBe(@"""C:\foo.exe""");
    }

    [Fact]
    public void BuildScCreateArgs_MultipleArgs_JoinedWithSingleSpaces()
    {
        var args = WindowsServiceHost.BuildScCreateArgs(new ServiceInstallRequest
        {
            ServiceName = "svc",
            Description = "Svc",
            ExecStart = @"C:\bin\foo.exe",
            ExecArgs = new[] { "run", "--instance", "named-instance" }
        });

        args[2].ShouldBe("binPath=");
        args[3].ShouldBe(@"""C:\bin\foo.exe"" run --instance named-instance");
    }

    [Fact]
    public void BuildScCreateArgs_EmptyDescription_FallsBackToServiceName()
    {
        // Description is the human-friendly name shown in services.msc;
        // missing it is non-fatal but DisplayName= is still required by sc.exe
        // for some operators' inventory tooling. Default to ServiceName.
        var args = WindowsServiceHost.BuildScCreateArgs(new ServiceInstallRequest
        {
            ServiceName = "squid-tentacle",
            Description = "",
            ExecStart = @"C:\foo.exe"
        });

        args[4].ShouldBe("DisplayName=");
        args[5].ShouldBe("squid-tentacle");
    }

    [Fact]
    public void BuildScCreateArgs_NullDescription_FallsBackToServiceName()
    {
        var args = WindowsServiceHost.BuildScCreateArgs(new ServiceInstallRequest
        {
            ServiceName = "squid-tentacle",
            Description = null,
            ExecStart = @"C:\foo.exe"
        });

        args[4].ShouldBe("DisplayName=");
        args[5].ShouldBe("squid-tentacle");
    }

    [Fact]
    public void BuildScCreateArgs_EmptyRunAsUser_DefaultsToLocalSystem()
    {
        // RunAsUser empty (WindowsServiceUserProvider returns ""
        // = "use platform default") → obj= LocalSystem. Explicit > implicit.
        var args = WindowsServiceHost.BuildScCreateArgs(new ServiceInstallRequest
        {
            ServiceName = "squid-tentacle",
            Description = "Svc",
            ExecStart = @"C:\foo.exe",
            RunAsUser = ""
        });

        args[8].ShouldBe("obj=");
        args[9].ShouldBe("LocalSystem");
    }

    [Fact]
    public void BuildScCreateArgs_NullRunAsUser_DefaultsToLocalSystem()
    {
        var args = WindowsServiceHost.BuildScCreateArgs(new ServiceInstallRequest
        {
            ServiceName = "squid-tentacle",
            Description = "Svc",
            ExecStart = @"C:\foo.exe",
            RunAsUser = null
        });

        args[8].ShouldBe("obj=");
        args[9].ShouldBe("LocalSystem");
    }

    [Fact]
    public void BuildScCreateArgs_CustomRunAsUser_PassedThrough()
    {
        // Future will add LSA grant + password= for
        // non-system identities. For now the value is passed through to
        // sc.exe verbatim — operator sees the SAME error if invalid.
        var args = WindowsServiceHost.BuildScCreateArgs(new ServiceInstallRequest
        {
            ServiceName = "squid-tentacle",
            Description = "Svc",
            ExecStart = @"C:\foo.exe",
            RunAsUser = @".\squid-tentacle"
        });

        args[8].ShouldBe("obj=");
        args[9].ShouldBe(@".\squid-tentacle");
    }

    // ── Trivial argv builders — pin the verbs ──────────────────────────────────

    [Fact]
    public void BuildScDeleteArgs_PinsVerbAndServiceName()
    {
        var args = WindowsServiceHost.BuildScDeleteArgs("squid-tentacle");
        args.ShouldBe(new[] { "delete", "squid-tentacle" });
    }

    [Fact]
    public void BuildScStartArgs_PinsVerbAndServiceName()
    {
        var args = WindowsServiceHost.BuildScStartArgs("squid-tentacle");
        args.ShouldBe(new[] { "start", "squid-tentacle" });
    }

    [Fact]
    public void BuildScStopArgs_PinsVerbAndServiceName()
    {
        var args = WindowsServiceHost.BuildScStopArgs("squid-tentacle");
        args.ShouldBe(new[] { "stop", "squid-tentacle" });
    }

    [Fact]
    public void BuildScQueryArgs_PinsVerbAndServiceName()
    {
        var args = WindowsServiceHost.BuildScQueryArgs("squid-tentacle");
        args.ShouldBe(new[] { "query", "squid-tentacle" });
    }

    // ── BuildScFailureArgs — restart-on-failure policy ──────────────

    [Fact]
    public void BuildScFailureArgs_DefaultValues_ProducesPinnedArgv()
    {
        // The canonical default: 3 restart actions, 10s between
        // restarts, 24h stable-runtime reset window. Mirrors systemd's
        // Restart=on-failure + RestartSec=10 + StartLimitBurst=3 trio.
        //
        // sc failure argv shape: each `key=` and value as TWO argvs, same
        // contract as BuildScCreateArgs.
        var args = WindowsServiceHost.BuildScFailureArgs("squid-tentacle");

        args.ShouldBe(new[]
        {
            "failure",
            "squid-tentacle",
            "reset=", "86400",
            "actions=", "restart/10000/restart/10000/restart/10000"
        });
    }

    [Fact]
    public void BuildScFailureArgs_NoArgOverload_RoutesThroughDefaultsViaConstants()
    {
        // The no-arg overload must derive its values from the public-const
        // defaults so renaming a constant doesn't silently drift the no-arg
        // path away from the explicit-args path.
        var defaults = WindowsServiceHost.BuildScFailureArgs("svc");

        var explicitArgs = WindowsServiceHost.BuildScFailureArgs(
            "svc",
            WindowsServiceHost.DefaultRestartCount,
            WindowsServiceHost.DefaultRestartDelayMs,
            WindowsServiceHost.DefaultResetSeconds);

        defaults.ShouldBe(explicitArgs);
    }

    [Theory]
    [InlineData(1, "restart/10000")]
    [InlineData(2, "restart/10000/restart/10000")]
    [InlineData(3, "restart/10000/restart/10000/restart/10000")]
    [InlineData(5, "restart/10000/restart/10000/restart/10000/restart/10000/restart/10000")]
    public void BuildScFailureArgs_RestartCount_BuildsActionsListWithCorrectLength(int restartCount, string expectedActionsTail)
    {
        // sc failure semantics: each entry in `actions=` consumes ONE failure.
        // After all entries are exhausted, additional failures are ignored
        // until `reset=` seconds of stable runtime. So restartCount=3 means
        // the SCM will try to restart 3 times then give up — matching
        // systemd's StartLimitBurst=3 + StartLimitAction=none semantics.
        var args = WindowsServiceHost.BuildScFailureArgs("svc", restartCount, 10_000, 86_400);

        args[4].ShouldBe("actions=");
        args[5].ShouldBe(expectedActionsTail);
    }

    [Fact]
    public void BuildScFailureArgs_CustomDelay_AppearsInEveryAction()
    {
        // The same delay is applied to every restart in the actions list — sc
        // failure doesn't support per-action delays (a real limitation vs
        // systemd's `RestartSteps`/`RestartMaxDelaySec` for backoff). Scope
        // is fixed delay matching systemd's `RestartSec=10`.
        var args = WindowsServiceHost.BuildScFailureArgs("svc", restartCount: 3, restartDelayMs: 30_000, resetSeconds: 86_400);

        args[4].ShouldBe("actions=");
        args[5].ShouldBe("restart/30000/restart/30000/restart/30000");
    }

    [Fact]
    public void BuildScFailureArgs_CustomReset_PinsResetValue()
    {
        var args = WindowsServiceHost.BuildScFailureArgs("svc", restartCount: 3, restartDelayMs: 10_000, resetSeconds: 600);

        args[2].ShouldBe("reset=");
        args[3].ShouldBe("600");
    }

    [Fact]
    public void BuildScFailureArgs_KeyValuePairs_AreSeparateArgvs()
    {
        // Reverse-verify guard: if a future refactor accidentally re-packs
        // `"key="` and value back into one `"key= value"` token, sc.exe would
        // bail with "Invalid type= field" / similar on the NEXT option. Pin
        // the per-token shape so any drift is compile/test-time visible.
        var args = WindowsServiceHost.BuildScFailureArgs("svc");

        // Each key has trailing '=' AND no embedded space (= ends the key token).
        args[2].ShouldBe("reset=");
        args[2].ShouldNotContain(" ", customMessage: "key token must NOT carry the value — sc.exe expects key and value as separate argvs");
        args[4].ShouldBe("actions=");
        args[4].ShouldNotContain(" ");

        // Values are bare strings — no embedded `key=` prefix.
        args[3].ShouldNotStartWith("reset");
        args[5].ShouldNotStartWith("actions");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildScFailureArgs_EmptyServiceName_Throws(string serviceName)
    {
        Should.Throw<ArgumentException>(() =>
            WindowsServiceHost.BuildScFailureArgs(serviceName));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void BuildScFailureArgs_NonPositiveRestartCount_Throws(int restartCount)
    {
        // restartCount < 1 means "no restart policy" — caller should skip the
        // sc failure invocation entirely rather than emit an empty actions
        // list. Throw to surface the misuse.
        Should.Throw<ArgumentOutOfRangeException>(() =>
            WindowsServiceHost.BuildScFailureArgs("svc", restartCount, 10_000, 86_400));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void BuildScFailureArgs_NegativeRestartDelay_Throws(int restartDelayMs)
    {
        // Zero is allowed (instant-restart, useful for tests / specific
        // operator policies), negative is nonsense.
        Should.Throw<ArgumentOutOfRangeException>(() =>
            WindowsServiceHost.BuildScFailureArgs("svc", 3, restartDelayMs, 86_400));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void BuildScFailureArgs_NegativeResetSeconds_Throws(int resetSeconds)
    {
        // Zero is allowed (sc.exe interprets reset=0 as "always reset", i.e.
        // every failure starts with a fresh restart budget), negative is
        // nonsense.
        Should.Throw<ArgumentOutOfRangeException>(() =>
            WindowsServiceHost.BuildScFailureArgs("svc", 3, 10_000, resetSeconds));
    }

    // ── Public API surface (constants + IsSupported) ───────────────────────────

    [Fact]
    public void DefaultServiceUser_ConstantPinned()
    {
        // Renaming this constant breaks every audit trail (Event Viewer, SCM
        // dumps, Splunk searches) that filters on the literal "LocalSystem".
        // Pin per Rule 8.
        WindowsServiceHost.DefaultServiceUser.ShouldBe("LocalSystem");
    }

    [Fact]
    public void ScExeFileName_ConstantPinned()
    {
        // PATH-resolved bare "sc.exe". Renaming would break operators on
        // stripped images that PATH-prepend a custom Windows directory.
        WindowsServiceHost.ScExeFileName.ShouldBe("sc.exe");
    }

    [Fact]
    public void DefaultRestartCount_ConstantPinned()
    {
        // Mirrors systemd's StartLimitBurst=3 (3 restart attempts then give
        // up). Operators may rely on this literal in monitoring runbooks
        // ("if you see 3 restart events in a row, escalate"). Pin per Rule 8.
        WindowsServiceHost.DefaultRestartCount.ShouldBe(3);
    }

    [Fact]
    public void DefaultRestartDelayMs_ConstantPinned()
    {
        // 10_000 ms = 10s. Mirrors systemd's RestartSec=10 in
        // SystemdServiceHost.BuildUnitFile. Pin per Rule 8 — drift between
        // Linux/Windows means operators see different restart cadences on
        // mixed-fleet deployments.
        WindowsServiceHost.DefaultRestartDelayMs.ShouldBe(10_000);
    }

    [Fact]
    public void DefaultResetSeconds_ConstantPinned()
    {
        // 86_400 s = 24h. Common Windows convention for "reset failure
        // counter after a full day of stable runtime". NOT directly
        // equivalent to systemd's StartLimitIntervalSec=120 (rolling-window
        // burst detection); chosen because Windows has no rolling-window
        // option in sc failure — only "stable for X seconds" reset.
        // 24h gives the SCM enough time to forget transient issues without
        // permanently disarming the policy.
        WindowsServiceHost.DefaultResetSeconds.ShouldBe(86_400);
    }

    [Fact]
    public void DisplayName_ReturnsHumanReadableLabel()
    {
        // Label appears in ServiceCommand error messages — operators see it
        // when something goes wrong. Pin to detect accidental rename.
#pragma warning disable CA1416 // [SupportedOSPlatform("windows")] is analyzer-only
        new WindowsServiceHost().DisplayName.ShouldBe("Windows Service Manager");
#pragma warning restore CA1416
    }

    [Fact]
    public void IsSupported_TrueOnWindowsOnly()
    {
        // ServiceHostFactory uses IsSupported to decide whether this host can
        // be picked. Confirm the OS gate matches OperatingSystem.IsWindows().
#pragma warning disable CA1416
        new WindowsServiceHost().IsSupported.ShouldBe(OperatingSystem.IsWindows());
#pragma warning restore CA1416
    }

    // ── Validation guards (cross-platform) ─────────────────────────────────────

    [Fact]
    public void Install_NullRequest_Throws()
    {
#pragma warning disable CA1416
        var host = new WindowsServiceHost();
#pragma warning restore CA1416

        Should.Throw<ArgumentNullException>(() => host.Install(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Install_EmptyServiceName_Throws(string serviceName)
    {
#pragma warning disable CA1416
        var host = new WindowsServiceHost();
#pragma warning restore CA1416

        Should.Throw<ArgumentException>(() => host.Install(new ServiceInstallRequest
        {
            ServiceName = serviceName,
            ExecStart = @"C:\foo.exe"
        }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Install_EmptyExecStart_Throws(string execStart)
    {
#pragma warning disable CA1416
        var host = new WindowsServiceHost();
#pragma warning restore CA1416

        Should.Throw<ArgumentException>(() => host.Install(new ServiceInstallRequest
        {
            ServiceName = "squid-tentacle",
            ExecStart = execStart
        }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Uninstall_EmptyServiceName_Throws(string serviceName)
    {
#pragma warning disable CA1416
        var host = new WindowsServiceHost();
#pragma warning restore CA1416

        Should.Throw<ArgumentException>(() => host.Uninstall(serviceName));
    }
}

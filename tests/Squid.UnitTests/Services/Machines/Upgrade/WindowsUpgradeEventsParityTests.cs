using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Tentacle.Platform;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Drift detector + parity pins for the Windows upgrade script's event timeline.
/// The Linux script emits upgrade-events.jsonl (per-phase timeline) which the
/// server surfaces; this PR adds the same to Windows. These tests read BOTH
/// embedded scripts and assert the Windows emission matches the Linux contract
/// (format, cap, terminal-kind set) and the path the agent reads
/// (<see cref="object"/> — WindowsUpgradeStatusStorage.EventsFileSubPath), so a
/// future edit to one script that diverges from the other is caught at build.
/// </summary>
public sealed class WindowsUpgradeEventsParityTests
{
    private const string LinuxResource = "Squid.Core.Resources.Upgrade.upgrade-linux-tentacle.sh";
    private const string WindowsResource = "Squid.Core.Resources.Upgrade.upgrade-windows-tentacle.ps1";

    private static string ReadScript(string resourceName)
    {
        var asm = typeof(WindowsTentacleUpgradeStrategy).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static readonly string Windows = ReadScript(WindowsResource);
    private static readonly string Linux = ReadScript(LinuxResource);

    [Fact]
    public void Windows_DefinesEventEmitter_WritingToEventsJsonl()
    {
        Windows.ShouldContain("function Write-UpgradeEvent", customMessage: "Windows must define an event emitter for parity with Linux emit_event.");
        Windows.ShouldContain("upgrade-events.jsonl", customMessage: "Windows must write the per-phase timeline to upgrade-events.jsonl.");
    }

    [Fact]
    public void Windows_EmitsCanonicalEventJsonShape()
    {
        // The server parses each line as UpgradeEvent {t, phase, kind, msg}.
        Windows.ShouldContain("\"t\":\"", customMessage: "event must carry the timestamp field 't'.");
        Windows.ShouldContain("\"phase\":\"");
        Windows.ShouldContain("\"kind\":\"");
        Windows.ShouldContain("\"msg\":\"");
    }

    [Fact]
    public void Windows_EventsCap_MatchesLinux()
    {
        var win = Regex.Match(Windows, @"\$EVENTS_MAX\s*=\s*(\d+)");
        var lin = Regex.Match(Linux, @"EVENTS_MAX=(\d+)");

        win.Success.ShouldBeTrue("Windows must define an $EVENTS_MAX cap.");
        lin.Success.ShouldBeTrue("Linux must define an EVENTS_MAX cap.");
        win.Groups[1].Value.ShouldBe(lin.Groups[1].Value,
            customMessage: "the per-run event cap must be identical across the two scripts.");
    }

    [Fact]
    public void Windows_TerminalKinds_MatchLinux()
    {
        // Linux: case "$kind" in success|rollback-ok|...|healthz-fail)
        var linuxList = Regex.Match(Linux, @"case ""\$kind"" in\s*\n\s*([a-z|\-]+)\)");
        linuxList.Success.ShouldBeTrue("could not locate the Linux terminal-kind case list.");
        var linuxKinds = linuxList.Groups[1].Value.Split('|').Select(k => k.Trim()).Where(k => k.Length > 0).ToHashSet();

        // Windows: $terminalKinds = @('success', 'rollback-ok', ...)
        var winList = Regex.Match(Windows, @"\$terminalKinds\s*=\s*@\(([^)]*)\)");
        winList.Success.ShouldBeTrue("could not locate the Windows $terminalKinds array.");
        var winKinds = Regex.Matches(winList.Groups[1].Value, @"'([^']+)'").Select(m => m.Groups[1].Value).ToHashSet();

        winKinds.ShouldBe(linuxKinds, ignoreOrder: true,
            customMessage: "Windows and Linux terminal-event kinds must stay identical — both feed the FE's " +
                           "stop-polling logic and the cap-bypass guarantee. If one changes, update the other.");
    }

    [Fact]
    public void Windows_TruncatesEventsFileBeforeRun()
    {
        // Phase A starts fresh (single invocation), like Linux's `: > "$EVENTS_FILE"`.
        Windows.ShouldContain("WriteAllText($EVENTS_FILE",
            customMessage: "Windows must truncate the events file at the start of the run so a prior attempt's events don't leak.");
    }

    [Fact]
    public void Windows_StatusTransitions_AutoEmitEvents()
    {
        // Write-UpgradeStatus maps each status → an event kind, so the timeline
        // tracks the status file 1:1 without per-call-site duplication.
        Windows.ShouldContain("switch ($Status)");
        foreach (var mapped in new[] { "'IN_PROGRESS'", "'SWAPPED'", "'SUCCESS'", "'FAILED'", "'ROLLED_BACK'" })
            Windows.ShouldContain(mapped, customMessage: $"status→event map must handle {mapped}.");
    }

    [Fact]
    public void Windows_EventsPath_MatchesAgentReadPath()
    {
        // The agent reads upgrade\upgrade-events.jsonl under the system config dir;
        // the script must write to the same relative location.
        WindowsUpgradeStatusStorage.EventsFileSubPath.ShouldBe(@"upgrade\upgrade-events.jsonl");
        Windows.ShouldContain("Squid\\Tentacle\\upgrade",
            customMessage: "Windows must write under %ProgramData%\\Squid\\Tentacle\\upgrade to match WindowsUpgradeStatusStorage.");
        Windows.ShouldContain("'upgrade-events.jsonl'");
    }

    [Fact]
    public void Windows_AppendsEventsWithoutBom()
    {
        // PS 5.1 Add-Content -Encoding UTF8 prepends a BOM that corrupts the
        // first JSON line. The script must use BOM-less .NET IO instead.
        Windows.ShouldContain("UTF8Encoding($false)",
            customMessage: "events must be appended BOM-less so the server can parse the first line.");
    }
}

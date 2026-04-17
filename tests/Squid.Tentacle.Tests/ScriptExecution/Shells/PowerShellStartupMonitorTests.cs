using System.Diagnostics;
using Shouldly;
using Squid.Tentacle.ScriptExecution.Shells;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution.Shells;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class PowerShellStartupMonitorTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-psmon-test-{Guid.NewGuid():N}");
    private readonly List<Process> _processesToKill = new();

    public PowerShellStartupMonitorTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        foreach (var p in _processesToKill)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
            try { p.Dispose(); } catch { /* ignore */ }
        }
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task SentinelAppears_Promptly_OutcomeIsSentinelAppeared()
    {
        var process = StartLongRunningBash();
        using var monitor = new PowerShellStartupMonitor(process, _workspace, TimeSpan.FromSeconds(5));

        var outcomeTask = monitor.StartAsync();
        await Task.Delay(200);
        File.WriteAllText(monitor.SentinelPath, "started");

        var outcome = await outcomeTask.WaitAsync(TimeSpan.FromSeconds(3));

        outcome.ShouldBe(StartupOutcome.SentinelAppeared);
        process.HasExited.ShouldBeFalse("monitor must not kill the process if startup succeeded");
    }

    [Fact]
    public async Task SentinelNeverAppears_KillsProcessAfterTimeout()
    {
        var process = StartLongRunningBash();
        using var monitor = new PowerShellStartupMonitor(process, _workspace, TimeSpan.FromMilliseconds(500));

        var outcome = await monitor.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));

        outcome.ShouldBe(StartupOutcome.TimedOut);
        process.WaitForExit(TimeSpan.FromSeconds(3)).ShouldBeTrue("timeout must kill the hung PowerShell process");
    }

    [Fact]
    public async Task ProcessExitsBeforeSentinel_OutcomeReflectsIt()
    {
        // Start a shell that exits immediately without touching the sentinel.
        var process = StartShortLivedBash();

        using var monitor = new PowerShellStartupMonitor(process, _workspace, TimeSpan.FromSeconds(5));
        var outcome = await monitor.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));

        outcome.ShouldBe(StartupOutcome.ProcessExitedBeforeSentinel);
    }

    [Fact]
    public void WrapScript_PrependsSentinelTouch()
    {
        var wrapped = PowerShellStartupMonitor.WrapScript(
            userScript: "Write-Host hello",
            sentinelPath: @"C:\Temp\shouldrun.txt");

        wrapped.ShouldStartWith("$null = New-Item -Force -Path 'C:\\Temp\\shouldrun.txt'");
        wrapped.ShouldContain("Write-Host hello");
    }

    [Fact]
    public void WrapScript_EscapesSingleQuotesInPath()
    {
        var wrapped = PowerShellStartupMonitor.WrapScript(
            userScript: "echo ok",
            sentinelPath: @"/tmp/foo's/shouldrun.txt");

        wrapped.ShouldContain("/tmp/foo''s/shouldrun.txt");
    }

    private Process StartLongRunningBash()
    {
        var psi = new ProcessStartInfo("bash", "-c \"sleep 30\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(psi)!;
        _processesToKill.Add(process);
        return process;
    }

    private Process StartShortLivedBash()
    {
        var psi = new ProcessStartInfo("bash", "-c \"exit 0\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(psi)!;
        _processesToKill.Add(process);
        return process;
    }
}

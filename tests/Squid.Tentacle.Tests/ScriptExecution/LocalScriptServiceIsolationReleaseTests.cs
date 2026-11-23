using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: HIGH — real <see cref="LocalScriptService"/>, real bash processes, the real isolation mutex.
/// Pins that isolation protects only the RUNNING process: once the script has exited the lock is free even if the
/// server never sends CompleteScript (its response was lost and retries ran out).
/// </summary>
[Trait("Category", "LinuxTentacleScriptExitPersistenceE2E")]
public class LocalScriptServiceIsolationReleaseTests : IDisposable
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(8);

    private readonly LocalScriptService _service = new();
    private readonly List<string> _tickets = new();
    private readonly string _mutex = $"iso-release-{Guid.NewGuid():N}";

    [Fact]
    public void FullIsolationScriptExited_NoComplete_NextFullIsolationScriptStartsPromptly()
    {
        if (OperatingSystem.IsWindows()) return;

        Start("echo first; exit 0", TimeSpan.FromSeconds(30));
        var sw = Stopwatch.StartNew();

        Should.NotThrow(() => Start("echo second", Prompt), "the first script exited; its writer lock must be free without any CompleteScript");

        sw.Elapsed.ShouldBeLessThan(Prompt);
    }

    [Fact]
    public void FullIsolationScriptStillRunning_StillBlocksNextFullIsolationScript()
    {
        if (OperatingSystem.IsWindows()) return;

        Start("sleep 20", TimeSpan.FromSeconds(30));

        Should.Throw<InvalidOperationException>(() => Start("echo never", TimeSpan.FromMilliseconds(600)), "isolation must keep protecting a running process");
    }

    [Fact]
    public void CompleteAfterExitRelease_ReturnsResultAndReleasesNothingTwice()
    {
        if (OperatingSystem.IsWindows()) return;

        var first = Start("echo first; exit 4", TimeSpan.FromSeconds(30));
        var second = Start("sleep 20", Prompt);

        var completed = _service.CompleteScript(new CompleteScriptCommand(first, 0));

        completed.ExitCode.ShouldBe(4);
        Should.Throw<InvalidOperationException>(() => Start("echo never", TimeSpan.FromMilliseconds(600)), "completing the first script must not release the lock the second script now holds");
    }

    [Fact]
    public void CancelRunningFullIsolationScript_ReleasesLock()
    {
        if (OperatingSystem.IsWindows()) return;

        var first = Start("sleep 20", TimeSpan.FromSeconds(30));

        _service.CancelScript(new CancelScriptCommand(first, 0));

        Should.NotThrow(() => Start("echo after-cancel", Prompt));
    }

    private ScriptTicket Start(string body, TimeSpan mutexTimeout)
    {
        var command = new StartScriptCommand(new ScriptTicket(Guid.NewGuid().ToString("N")), body, ScriptIsolationLevel.FullIsolation, mutexTimeout, _mutex, Array.Empty<string>(), null, TimeSpan.Zero);
        _tickets.Add(command.ScriptTicket.TaskId);
        _service.StartScript(command);

        return command.ScriptTicket;
    }

    public void Dispose()
    {
        foreach (var id in _tickets)
        {
            try { _service.CancelScript(new CancelScriptCommand(new ScriptTicket(id), 0)); } catch { }
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), $"squid-tentacle-{id}"), recursive: true); } catch { }
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using k8s.Models;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.V2;
using Squid.Tentacle.Tests.Health;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: MEDIUM-MOCK — the real <see cref="ScriptPodService"/> over a mocked Kubernetes API that keeps the Secrets it is given. A queued
/// script's pending Secret (which carries its script body and makes a restart report it lost) lives exactly as long as the ticket is queued:
/// whichever way the ticket leaves the queue — dequeued, cancelled, timed out, failed to launch — and in whatever order that races the write, no
/// Secret is left behind.
/// </summary>
[Collection(TentacleMetricsCollection.Name)]
public class ScriptPodServicePendingSecretTests : IDisposable
{
    private readonly Mock<IKubernetesPodOperations> _ops = new();
    private readonly ConcurrentDictionary<string, byte> _secrets = new();
    private readonly KubernetesSettings _kubernetesSettings = new() { TentacleNamespace = "test-ns", ScriptPodImage = "test-image@sha256:abc123def456789012345678901234567890123456789012345678901234aa77", PvcClaimName = "test-pvc" };
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-pod-pending-secret-{Guid.NewGuid():N}");

    public ScriptPodServicePendingSecretTests()
    {
        DiskSpaceChecker.Enabled = false;
        Directory.CreateDirectory(_workspace);

        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>())).Returns((V1Pod pod, string ns) => pod);
        _ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>())).Returns(new V1PodList { Items = new List<V1Pod>() });
        _ops.Setup(o => o.ReadPodLog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>())).Returns(() => new MemoryStream());
        _ops.Setup(o => o.ReadPodStatus(It.IsAny<string>(), It.IsAny<string>())).Returns(new V1Pod { Status = new V1PodStatus { Phase = "Running" } });
        _ops.Setup(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), It.IsAny<string>())).Returns((V1Secret secret, string ns) => { _secrets[secret.Metadata.Name] = 0; return secret; });
        _ops.Setup(o => o.DeleteSecret(It.IsAny<string>(), It.IsAny<string>())).Callback((string name, string ns) => _secrets.TryRemove(name, out _));
    }

    [Theory]
    [InlineData("CancelV1")]
    [InlineData("CancelV2")]
    [InlineData("Dequeue")]
    public void Start_TheTicketLeavesTheQueueBeforeItsSecretIsWritten_NoSecretIsLeftBehind(string leave)
    {
        var service = NewService();
        var holder = NewCommand();
        var queued = NewCommand();
        service.StartScript(holder).State.ShouldBe(ScriptStateV2.Running);

        // Start queued the script and released the ticket lock; before it writes the pending Secret, the ticket leaves the queue.
        service.AfterStartDecisionHookForTests = () =>
        {
            service.AfterStartDecisionHookForTests = null;
            LeaveQueue(service, leave, holder, queued);
            service.PendingScripts.ContainsKey(queued.ScriptTicket.TaskId).ShouldBeFalse();
        };

        service.StartScript(queued);

        _secrets.ContainsKey(SecretOf(queued)).ShouldBeFalse("the Secret was written after the ticket left the queue: nobody else will ever delete it");
    }

    [Fact]
    public void GetStatus_AQueuedScriptTimesOut_ItsSecretIsDeleted()
    {
        _kubernetesSettings.IsolationMutexTimeoutMinutes = 0;
        var service = NewService();
        var holder = NewCommand();
        var queued = NewCommand();
        service.StartScript(holder).State.ShouldBe(ScriptStateV2.Running);
        service.StartScript(queued).State.ShouldBe(ScriptStateV2.Pending);
        _secrets.ContainsKey(SecretOf(queued)).ShouldBeTrue("a script still queued keeps its Secret");

        service.GetStatus(new ScriptStatusRequest(queued.ScriptTicket, 0)).ExitCode.ShouldBe(ScriptExitCodes.Timeout);

        _secrets.ContainsKey(SecretOf(queued)).ShouldBeFalse("a timed-out script is no longer queued: a restart must not report it lost again");
    }

    [Fact]
    public void Dequeue_TheQueuedScriptsLaunchFails_ItsSecretIsDeleted()
    {
        var service = NewService();
        var holder = NewCommand();
        var queued = NewCommand();
        service.StartScript(holder).State.ShouldBe(ScriptStateV2.Running);
        service.StartScript(queued).State.ShouldBe(ScriptStateV2.Pending);
        _ops.Setup(o => o.CreatePod(It.Is<V1Pod>(p => p.Metadata.Name == PodOf(queued)), It.IsAny<string>())).Throws(new InvalidOperationException("quota exceeded"));

        service.CancelScript(new CancelScriptCommandV2(holder.ScriptTicket, 0));

        service.GetStatus(new ScriptStatusRequest(queued.ScriptTicket, 0)).ExitCode.ShouldBe(ScriptExitCodes.Fatal);
        _secrets.ContainsKey(SecretOf(queued)).ShouldBeFalse("a script whose launch failed is no longer queued");
    }

    private static void LeaveQueue(ScriptPodService service, string leave, StartScriptCommandV2 holder, StartScriptCommandV2 queued)
    {
        switch (leave)
        {
            case "CancelV1": service.CancelScript(new CancelScriptCommand(queued.ScriptTicket, 0)); break;
            case "CancelV2": service.CancelScript(new CancelScriptCommandV2(queued.ScriptTicket, 0)); break;
            default: service.CancelScript(new CancelScriptCommandV2(holder.ScriptTicket, 0)); break;
        }
    }

    private ScriptPodService NewService() => new(new TentacleSettings { WorkspacePath = _workspace }, _kubernetesSettings, new KubernetesPodManager(_ops.Object, _kubernetesSettings), _ops.Object);

    private static StartScriptCommandV2 NewCommand()
        => new() { ScriptTicket = new ScriptTicket(Guid.NewGuid().ToString("N")), ScriptBody = "echo hi", Isolation = ScriptIsolationLevel.FullIsolation, IsolationMutexName = "pending-secret-mx", ScriptIsolationMutexTimeout = TimeSpan.FromMinutes(5) };

    private static string SecretOf(StartScriptCommandV2 command) => $"squid-pending-{command.ScriptTicket.TaskId[..12]}";

    private static string PodOf(StartScriptCommandV2 command) => $"squid-script-{command.ScriptTicket.TaskId[..12]}";

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); } catch { }
    }
}

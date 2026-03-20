using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using k8s.Models;
using Squid.Message.Constants;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class KubernetesPodManagerLifecycleTests
{
    private readonly Mock<IKubernetesPodOperations> _ops = new();
    private readonly KubernetesPodManager _manager;

    public KubernetesPodManagerLifecycleTests()
    {
        var settings = new KubernetesSettings { TentacleNamespace = "test-ns" };
        _manager = new KubernetesPodManager(_ops.Object, settings);
    }

    // === GetPodPhase ===

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    [InlineData("Running")]
    [InlineData("Pending")]
    public void GetPodPhase_ReturnsPhase(string phase)
    {
        _ops.Setup(o => o.ReadPodStatus("pod-1", "test-ns"))
            .Returns(new V1Pod { Status = new V1PodStatus { Phase = phase } });

        _manager.GetPodPhase("pod-1").ShouldBe(phase);
    }

    [Fact]
    public void GetPodPhase_PodNotFound_ReturnsNotFoundSentinel()
    {
        _ops.Setup(o => o.ReadPodStatus("missing", "test-ns"))
            .Throws(CreateHttpOperationException(HttpStatusCode.NotFound));

        _manager.GetPodPhase("missing").ShouldBe(KubernetesPodManager.PhaseNotFound);
    }

    // === GetPodExitCode ===

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(137)]
    public void GetPodExitCode_ReturnsExitCode(int exitCode)
    {
        _ops.Setup(o => o.ReadPodStatus("pod-1", "test-ns"))
            .Returns(new V1Pod
            {
                Status = new V1PodStatus
                {
                    ContainerStatuses = new List<V1ContainerStatus>
                    {
                        new()
                        {
                            Name = "script",
                            State = new V1ContainerState
                            {
                                Terminated = new V1ContainerStateTerminated { ExitCode = exitCode }
                            }
                        }
                    }
                }
            });

        _manager.GetPodExitCode("pod-1").ShouldBe(exitCode);
    }

    [Fact]
    public void GetPodExitCode_NoTerminatedState_ReturnsPodNotFound()
    {
        _ops.Setup(o => o.ReadPodStatus("pod-1", "test-ns"))
            .Returns(new V1Pod
            {
                Status = new V1PodStatus
                {
                    ContainerStatuses = new List<V1ContainerStatus>
                    {
                        new()
                        {
                            Name = "script",
                            State = new V1ContainerState { Running = new V1ContainerStateRunning() }
                        }
                    }
                }
            });

        _manager.GetPodExitCode("pod-1").ShouldBe(ScriptExitCodes.PodNotFound);
    }

    [Fact]
    public void GetPodExitCode_ExceptionThrown_ReturnsPodNotFound()
    {
        _ops.Setup(o => o.ReadPodStatus("pod-1", "test-ns"))
            .Throws(new Exception("connection refused"));

        _manager.GetPodExitCode("pod-1").ShouldBe(ScriptExitCodes.PodNotFound);
    }

    // === ReadPodLogs ===

    [Fact]
    public void ReadPodLogs_ReturnsContent()
    {
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello world"));
        _ops.Setup(o => o.ReadPodLog("pod-1", "test-ns", "script")).Returns(stream);

        _manager.ReadPodLogs("pod-1").ShouldBe("hello world");
    }

    [Fact]
    public void ReadPodLogs_ExceptionThrown_ReturnsEmpty()
    {
        _ops.Setup(o => o.ReadPodLog("pod-1", "test-ns", "script"))
            .Throws(new Exception("timeout"));

        _manager.ReadPodLogs("pod-1").ShouldBe(string.Empty);
    }

    // === DeletePod ===

    [Fact]
    public void DeletePod_Success_CallsOps()
    {
        _manager.DeletePod("pod-1");

        _ops.Verify(o => o.DeletePod("pod-1", "test-ns"), Times.Once);
    }

    [Fact]
    public void DeletePod_NotFound_DoesNotThrow()
    {
        _ops.Setup(o => o.DeletePod("missing", "test-ns"))
            .Throws(CreateHttpOperationException(HttpStatusCode.NotFound));

        Should.NotThrow(() => _manager.DeletePod("missing"));
    }

    [Fact]
    public void DeletePod_OtherException_DoesNotThrow()
    {
        _ops.Setup(o => o.DeletePod("pod-1", "test-ns"))
            .Throws(new Exception("network error"));

        Should.NotThrow(() => _manager.DeletePod("pod-1"));
    }

    // === WaitForPodTermination ===

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    public void WaitForPodTermination_TerminalPhase_ReturnsImmediately(string phase)
    {
        _ops.Setup(o => o.ReadPodStatus("pod-1", "test-ns"))
            .Returns(new V1Pod { Status = new V1PodStatus { Phase = phase } });

        _manager.WaitForPodTermination("pod-1", TimeSpan.FromSeconds(5));

        _ops.Verify(o => o.ReadPodStatus("pod-1", "test-ns"), Times.Once);
    }

    [Fact]
    public void WaitForPodTermination_PodNotFound_ReturnsImmediately()
    {
        _ops.Setup(o => o.ReadPodStatus("pod-1", "test-ns"))
            .Throws(CreateHttpOperationException(HttpStatusCode.NotFound));

        _manager.WaitForPodTermination("pod-1", TimeSpan.FromSeconds(5));

        _ops.Verify(o => o.ReadPodStatus("pod-1", "test-ns"), Times.Once);
    }

    // === FindPodByTicket ===

    [Fact]
    public void FindPodByTicket_PodExists_ReturnsPodName()
    {
        _ops.Setup(o => o.ListPods("test-ns", "squid.io/ticket-id=abc123"))
            .Returns(new V1PodList
            {
                Items = new List<V1Pod>
                {
                    new() { Metadata = new V1ObjectMeta { Name = "squid-script-abc123" } }
                }
            });

        _manager.FindPodByTicket("abc123").ShouldBe("squid-script-abc123");
    }

    [Fact]
    public void FindPodByTicket_NoPod_ReturnsNull()
    {
        _ops.Setup(o => o.ListPods("test-ns", "squid.io/ticket-id=abc123"))
            .Returns(new V1PodList { Items = new List<V1Pod>() });

        _manager.FindPodByTicket("abc123").ShouldBeNull();
    }

    [Fact]
    public void FindPodByTicket_Exception_ReturnsNull()
    {
        _ops.Setup(o => o.ListPods("test-ns", It.IsAny<string>()))
            .Throws(new Exception("api timeout"));

        _manager.FindPodByTicket("abc123").ShouldBeNull();
    }

    // === CreatePod Idempotency ===

    [Fact]
    public void CreatePod_NoPodExists_CreatesPod()
    {
        SetupNoPodFound();
        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"))
            .Returns((V1Pod pod, string ns) => pod);

        var podName = _manager.CreatePod("abcdef123456extra");

        podName.ShouldStartWith("squid-script-");
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Once);
    }

    [Fact]
    public void CreatePod_PodAlreadyExists_SkipsCreate()
    {
        var ticketId = "abcdef123456extra";

        _ops.Setup(o => o.ListPods("test-ns", $"squid.io/ticket-id={ticketId}"))
            .Returns(new V1PodList
            {
                Items = new List<V1Pod>
                {
                    new() { Metadata = new V1ObjectMeta { Name = "squid-script-existing" } }
                }
            });

        var podName = _manager.CreatePod(ticketId);

        podName.ShouldBe("squid-script-existing");
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()), Times.Never);
    }

    // === ListManagedPods ===

    [Fact]
    public void ListManagedPods_NoReleaseName_UsesBaseSelector()
    {
        _ops.Setup(o => o.ListPods("test-ns", "app.kubernetes.io/managed-by=kubernetes-agent"))
            .Returns(new V1PodList { Items = new List<V1Pod>() });

        _manager.ListManagedPods();

        _ops.Verify(o => o.ListPods("test-ns", "app.kubernetes.io/managed-by=kubernetes-agent"), Times.Once);
    }

    [Fact]
    public void ListManagedPods_WithReleaseName_IncludesInstanceSelector()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        var settings = new KubernetesSettings { TentacleNamespace = "test-ns", ReleaseName = "squid-prod" };
        var manager = new KubernetesPodManager(ops.Object, settings);

        ops.Setup(o => o.ListPods("test-ns", "app.kubernetes.io/managed-by=kubernetes-agent,app.kubernetes.io/instance=squid-prod"))
            .Returns(new V1PodList { Items = new List<V1Pod>() });

        manager.ListManagedPods();

        ops.Verify(o => o.ListPods("test-ns", "app.kubernetes.io/managed-by=kubernetes-agent,app.kubernetes.io/instance=squid-prod"), Times.Once);
    }

    // === Helpers ===

    private void SetupNoPodFound()
    {
        _ops.Setup(o => o.ListPods("test-ns", It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });
    }

    private static k8s.Autorest.HttpOperationException CreateHttpOperationException(HttpStatusCode statusCode)
    {
        return new k8s.Autorest.HttpOperationException
        {
            Response = new k8s.Autorest.HttpResponseMessageWrapper(
                new System.Net.Http.HttpResponseMessage(statusCode), string.Empty)
        };
    }
}

using k8s;
using k8s.Autorest;
using k8s.Models;
using Moq;
using Squid.Tentacle.Watchdog;

namespace Squid.Tentacle.Watchdog.Tests;

public class PodTerminatorTests
{
    private readonly Mock<IKubernetes> _k8sMock = new();
    private readonly Mock<ICoreV1Operations> _coreV1Mock = new();
    private readonly Mock<IEventsV1Operations> _eventsMock = new();

    public PodTerminatorTests()
    {
        _k8sMock.Setup(k => k.CoreV1).Returns(_coreV1Mock.Object);
        _k8sMock.Setup(k => k.EventsV1).Returns(_eventsMock.Object);
    }

    [Fact]
    public async Task TerminateAsync_DeletesPodWithForegroundPropagation()
    {
        SetupDeletePod();
        SetupCreateEvent();

        var terminator = new PodTerminator(_k8sMock.Object, "my-pod", "my-ns");

        await terminator.TerminateAsync(CancellationToken.None);

        _coreV1Mock.Verify(c => c.DeleteNamespacedPodWithHttpMessagesAsync(
            "my-pod", "my-ns",
            It.IsAny<V1DeleteOptions>(), null, null, null,
            "Foreground", null, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TerminateAsync_RaisesK8sEventBeforeDelete()
    {
        var callOrder = new List<string>();

        _eventsMock
            .Setup(e => e.CreateNamespacedEventWithHttpMessagesAsync(It.IsAny<Eventsv1Event>(), "my-ns", null, null, null, null, null, It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("event"))
            .ReturnsAsync(new HttpOperationResponse<Eventsv1Event> { Body = new Eventsv1Event() });

        _coreV1Mock
            .Setup(c => c.DeleteNamespacedPodWithHttpMessagesAsync("my-pod", "my-ns", It.IsAny<V1DeleteOptions>(), null, null, null, "Foreground", null, null, It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("delete"))
            .ReturnsAsync(new HttpOperationResponse<V1Pod> { Body = new V1Pod() });

        var terminator = new PodTerminator(_k8sMock.Object, "my-pod", "my-ns");

        await terminator.TerminateAsync(CancellationToken.None);

        callOrder.ShouldBe(new[] { "event", "delete" });
    }

    [Fact]
    public async Task TerminateAsync_EventFails_StillDeletesPod()
    {
        _eventsMock
            .Setup(e => e.CreateNamespacedEventWithHttpMessagesAsync(It.IsAny<Eventsv1Event>(), "my-ns", null, null, null, null, null, It.IsAny<CancellationToken>()))
            .Throws(new Exception("event creation failed"));

        SetupDeletePod();

        var terminator = new PodTerminator(_k8sMock.Object, "my-pod", "my-ns");

        await terminator.TerminateAsync(CancellationToken.None);

        _coreV1Mock.Verify(c => c.DeleteNamespacedPodWithHttpMessagesAsync(
            "my-pod", "my-ns", It.IsAny<V1DeleteOptions>(),
            null, null, null, "Foreground", null, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TerminateAsync_DeleteFails_Throws()
    {
        SetupCreateEvent();

        _coreV1Mock
            .Setup(c => c.DeleteNamespacedPodWithHttpMessagesAsync("my-pod", "my-ns", It.IsAny<V1DeleteOptions>(), null, null, null, "Foreground", null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("delete failed"));

        var terminator = new PodTerminator(_k8sMock.Object, "my-pod", "my-ns");

        await Should.ThrowAsync<Exception>(() => terminator.TerminateAsync(CancellationToken.None));
    }

    private void SetupDeletePod()
    {
        _coreV1Mock
            .Setup(c => c.DeleteNamespacedPodWithHttpMessagesAsync("my-pod", "my-ns", It.IsAny<V1DeleteOptions>(), null, null, null, "Foreground", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpOperationResponse<V1Pod> { Body = new V1Pod() });
    }

    private void SetupCreateEvent()
    {
        _eventsMock
            .Setup(e => e.CreateNamespacedEventWithHttpMessagesAsync(It.IsAny<Eventsv1Event>(), "my-ns", null, null, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpOperationResponse<Eventsv1Event> { Body = new Eventsv1Event() });
    }
}

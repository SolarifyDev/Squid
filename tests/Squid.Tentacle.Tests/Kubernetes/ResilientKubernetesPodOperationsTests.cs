using System;
using System.Net;
using System.Net.Http;
using k8s.Models;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class ResilientKubernetesPodOperationsTests
{
    private readonly Mock<IKubernetesPodOperations> _inner = new();
    private readonly ResilientKubernetesPodOperations _resilient;

    public ResilientKubernetesPodOperationsTests()
    {
        _resilient = new ResilientKubernetesPodOperations(_inner.Object);
    }

    [Fact]
    public void ReadPodStatus_Success_DelegatesToInner()
    {
        var expected = new V1Pod { Metadata = new V1ObjectMeta { Name = "pod-1" } };
        _inner.Setup(o => o.ReadPodStatus("pod-1", "ns")).Returns(expected);

        var result = _resilient.ReadPodStatus("pod-1", "ns");

        result.ShouldBe(expected);
        _inner.Verify(o => o.ReadPodStatus("pod-1", "ns"), Times.Once);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public void ReadPodStatus_TransientError_Retries(HttpStatusCode statusCode)
    {
        var callCount = 0;
        var expected = new V1Pod { Metadata = new V1ObjectMeta { Name = "pod-1" } };

        _inner.Setup(o => o.ReadPodStatus("pod-1", "ns"))
            .Returns(() =>
            {
                callCount++;
                if (callCount <= 2)
                    throw CreateHttpOperationException(statusCode);
                return expected;
            });

        var result = _resilient.ReadPodStatus("pod-1", "ns");

        result.ShouldBe(expected);
        callCount.ShouldBe(3);
    }

    [Fact]
    public void ReadPodStatus_NonTransientError_DoesNotRetry()
    {
        _inner.Setup(o => o.ReadPodStatus("pod-1", "ns"))
            .Throws(CreateHttpOperationException(HttpStatusCode.NotFound));

        Should.Throw<k8s.Autorest.HttpOperationException>(() =>
            _resilient.ReadPodStatus("pod-1", "ns"));

        _inner.Verify(o => o.ReadPodStatus("pod-1", "ns"), Times.Once);
    }

    [Fact]
    public void ReadPodStatus_HttpRequestException_Retries()
    {
        var callCount = 0;
        var expected = new V1Pod { Metadata = new V1ObjectMeta { Name = "pod-1" } };

        _inner.Setup(o => o.ReadPodStatus("pod-1", "ns"))
            .Returns(() =>
            {
                callCount++;
                if (callCount <= 1)
                    throw new HttpRequestException("connection refused");
                return expected;
            });

        var result = _resilient.ReadPodStatus("pod-1", "ns");

        result.ShouldBe(expected);
        callCount.ShouldBe(2);
    }

    [Fact]
    public void ReadPodStatus_ExhaustsRetries_Throws()
    {
        _inner.Setup(o => o.ReadPodStatus("pod-1", "ns"))
            .Throws(CreateHttpOperationException(HttpStatusCode.ServiceUnavailable));

        Should.Throw<k8s.Autorest.HttpOperationException>(() =>
            _resilient.ReadPodStatus("pod-1", "ns"));

        _inner.Verify(o => o.ReadPodStatus("pod-1", "ns"), Times.Exactly(6));
    }

    [Fact]
    public void CreatePod_TransientError_Retries()
    {
        var callCount = 0;
        var pod = new V1Pod { Metadata = new V1ObjectMeta { Name = "pod-1" } };

        _inner.Setup(o => o.CreatePod(pod, "ns"))
            .Returns(() =>
            {
                callCount++;
                if (callCount <= 1)
                    throw CreateHttpOperationException(HttpStatusCode.ServiceUnavailable);
                return pod;
            });

        var result = _resilient.CreatePod(pod, "ns");

        result.ShouldBe(pod);
        callCount.ShouldBe(2);
    }

    [Fact]
    public void DeletePod_TransientError_Retries()
    {
        var callCount = 0;

        _inner.Setup(o => o.DeletePod("pod-1", "ns"))
            .Callback(() =>
            {
                callCount++;
                if (callCount <= 1)
                    throw CreateHttpOperationException(HttpStatusCode.GatewayTimeout);
            });

        _resilient.DeletePod("pod-1", "ns");

        callCount.ShouldBe(2);
    }

    [Fact]
    public void ListPods_TransientError_Retries()
    {
        var callCount = 0;
        var expected = new V1PodList { Items = new List<V1Pod>() };

        _inner.Setup(o => o.ListPods("ns", "label=value"))
            .Returns(() =>
            {
                callCount++;
                if (callCount <= 1)
                    throw new HttpRequestException("timeout");
                return expected;
            });

        var result = _resilient.ListPods("ns", "label=value");

        result.ShouldBe(expected);
        callCount.ShouldBe(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Conflict)]
    public void ReadPodStatus_NonTransientStatusCode_DoesNotRetry(HttpStatusCode statusCode)
    {
        _inner.Setup(o => o.ReadPodStatus("pod-1", "ns"))
            .Throws(CreateHttpOperationException(statusCode));

        Should.Throw<k8s.Autorest.HttpOperationException>(() =>
            _resilient.ReadPodStatus("pod-1", "ns"));

        _inner.Verify(o => o.ReadPodStatus("pod-1", "ns"), Times.Once);
    }

    private static k8s.Autorest.HttpOperationException CreateHttpOperationException(HttpStatusCode statusCode)
    {
        return new k8s.Autorest.HttpOperationException
        {
            Response = new k8s.Autorest.HttpResponseMessageWrapper(
                new HttpResponseMessage(statusCode), string.Empty)
        };
    }
}

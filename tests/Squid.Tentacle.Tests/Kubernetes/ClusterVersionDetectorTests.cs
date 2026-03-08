using System;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class ClusterVersionDetectorTests
{
    // ========================================================================
    // WarnIfBelowMinimum — version comparison logic
    // ========================================================================

    [Theory]
    [InlineData("v1.28.3")]
    [InlineData("v1.25.0")]
    [InlineData("v1.30.0-rc.1")]
    public void WarnIfBelowMinimum_AboveOrAtMinimum_DoesNotThrow(string gitVersion)
    {
        // Should not throw — only logs a warning
        ClusterVersionDetector.WarnIfBelowMinimum(gitVersion);
    }

    [Theory]
    [InlineData("v1.24.17")]
    [InlineData("v1.23.0")]
    [InlineData("v1.20.0-eks")]
    public void WarnIfBelowMinimum_BelowMinimum_DoesNotThrow(string gitVersion)
    {
        // Should log a warning but not throw
        ClusterVersionDetector.WarnIfBelowMinimum(gitVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void WarnIfBelowMinimum_InvalidVersion_DoesNotThrow(string gitVersion)
    {
        // Should handle gracefully
        ClusterVersionDetector.WarnIfBelowMinimum(gitVersion);
    }

    // ========================================================================
    // Basic properties
    // ========================================================================

    [Fact]
    public void Name_ReturnsClusterVersionDetection()
    {
        var detector = new ClusterVersionDetector(new Mock<k8s.IKubernetes>().Object);

        detector.Name.ShouldBe("ClusterVersionDetection");
    }

    [Fact]
    public void DetectedVersion_InitiallyNull()
    {
        var detector = new ClusterVersionDetector(new Mock<k8s.IKubernetes>().Object);

        detector.DetectedVersion.ShouldBeNull();
    }

    [Fact]
    public void Metadata_InitiallyEmpty()
    {
        var detector = new ClusterVersionDetector(new Mock<k8s.IKubernetes>().Object);

        detector.Metadata.ShouldNotBeNull();
        detector.Metadata.ShouldBeEmpty();
    }

    // ========================================================================
    // RunAsync — error resilience
    // ========================================================================

    [Fact]
    public async Task RunAsync_ApiFailure_DoesNotThrow()
    {
        var client = new Mock<k8s.IKubernetes>();
        client.Setup(c => c.Version).Throws(new Exception("API unavailable"));

        var detector = new ClusterVersionDetector(client.Object);

        // Should not throw — logs warning instead
        await detector.RunAsync(CancellationToken.None);

        detector.DetectedVersion.ShouldBeNull();
    }
}

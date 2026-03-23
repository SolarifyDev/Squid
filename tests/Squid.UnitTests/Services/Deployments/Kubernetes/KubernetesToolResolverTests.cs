using System;
using System.IO;
using Squid.Core.Services.DeploymentExecution.Kubernetes;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesToolResolverTests
{
    [Fact]
    public void ResolveKubectlPath_NoVersion_ReturnsEmpty()
    {
        var result = KubernetesToolResolver.ResolveKubectlPath(null);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ResolveKubectlPath_EmptyVersion_ReturnsEmpty()
    {
        var result = KubernetesToolResolver.ResolveKubectlPath(string.Empty);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ResolveKubectlPath_VersionNotFound_ReturnsEmpty()
    {
        var result = KubernetesToolResolver.ResolveKubectlPath("1.99.0", "/nonexistent/path");

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ResolveKubectlPath_VersionExists_ReturnsVersionedPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"squid-tools-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var kubectlPath = Path.Combine(tempDir, "kubectl-1.28.0");
        File.WriteAllText(kubectlPath, "stub");

        try
        {
            var result = KubernetesToolResolver.ResolveKubectlPath("1.28.0", tempDir);

            result.ShouldBe(kubectlPath);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ResolveHelmPath_NoVersion_ReturnsEmpty()
    {
        var result = KubernetesToolResolver.ResolveHelmPath(null);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ResolveHelmPath_VersionExists_ReturnsVersionedPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"squid-tools-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var helmPath = Path.Combine(tempDir, "helm-3.14.0");
        File.WriteAllText(helmPath, "stub");

        try
        {
            var result = KubernetesToolResolver.ResolveHelmPath("3.14.0", tempDir);

            result.ShouldBe(helmPath);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}

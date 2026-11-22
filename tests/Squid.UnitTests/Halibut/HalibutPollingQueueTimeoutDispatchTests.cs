using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.ServiceModel;
using Shouldly;
using Squid.Core.Halibut;
using Squid.Core.Services.Machines;
using Squid.Message.Contracts.Tentacle;
using Xunit;

namespace Squid.UnitTests.Halibut;

/// <summary>
/// Tier: HIGH at the Halibut seam — real <see cref="HalibutRuntime"/> built from the production SSOT limits,
/// real request queue, real endpoint from <see cref="EndpointJsonHelper"/>; the only absent piece is the
/// agent (a polling subscription nobody ever polls). No DB / pipeline. Proves SQUID_HALIBUT_POLLING_QUEUE_TIMEOUT_SECONDS
/// really bounds a dispatch (pre-fix the endpoint carried Halibut's 2-minute default regardless of the env var).
/// </summary>
[Collection(HalibutEnvVarCollection.Name)]
public sealed class HalibutPollingQueueTimeoutDispatchTests
{
    [Fact]
    public async Task PollingDispatch_ToAgentThatNeverPolls_FailsWithinConfiguredQueueTimeout()
    {
        using var cert = CreateSelfSignedCert();

        Environment.SetEnvironmentVariable(HalibutModule.PollingRequestQueueTimeoutSecondsEnvVar, "3");

        try
        {
            using var runtime = new HalibutRuntimeBuilder()
                .WithServiceFactory(new DelegateServiceFactory())
                .WithServerCertificate(cert)
                .WithHalibutTimeoutsAndLimits(HalibutModule.BuildTimeoutsAndLimits())
                .Build();

            var endpoint = EndpointJsonHelper.ParseHalibutEndpoint($"{{\"SubscriptionId\":\"never-polls-{Guid.NewGuid():N}\",\"Thumbprint\":\"{cert.Thumbprint}\"}}");
            var client = runtime.CreateAsyncClient<ICapabilitiesService, IAsyncCapabilitiesService>(endpoint);

            var stopwatch = Stopwatch.StartNew();
            var ex = await Record.ExceptionAsync(() => client.GetCapabilitiesAsync(new CapabilitiesRequest()));
            stopwatch.Stop();

            ex.ShouldNotBeNull("dispatch to a never-polling agent must fail, not succeed");
            stopwatch.Elapsed.TotalSeconds.ShouldBeLessThan(30, $"dispatch took {stopwatch.Elapsed.TotalSeconds:F1}s with SQUID_HALIBUT_POLLING_QUEUE_TIMEOUT_SECONDS=3 (Halibut default is 120s). The env override is not reaching the ServiceEndPoint — check EndpointJsonHelper uses HalibutModule.BuildTimeoutsAndLimits(). Exception: {ex}");
            stopwatch.Elapsed.TotalSeconds.ShouldBeGreaterThan(2, "failed before the queue timeout elapsed — failure is not the queue timeout; investigate: " + ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HalibutModule.PollingRequestQueueTimeoutSecondsEnvVar, null);
        }
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=queue-timeout-test-{Guid.NewGuid():N}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));

        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, string.Empty), string.Empty, X509KeyStorageFlags.Exportable);
    }
}

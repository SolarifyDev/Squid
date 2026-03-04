namespace Squid.IntegrationTests.Deployments.Certificates;

[Collection("Certificate Tests")]
public class CertificateFixtureBase : TestBase
{
    protected CertificateFixtureBase() : base("_certificate_", "squid_test_certificate") { }
}

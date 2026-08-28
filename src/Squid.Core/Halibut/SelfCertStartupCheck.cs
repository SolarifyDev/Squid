using System.Security.Cryptography.X509Certificates;
using Squid.Core.Settings.SelfCert;

namespace Squid.Core.Halibut;

/// <summary>
/// Runs the published-identity check once, synchronously, at container build.
///
/// <para><b>Why at build and not only inside the <c>HalibutRuntime</c> factory</b>: the runtime
/// is a lazily-resolved singleton, and the one component that touches it at startup
/// (<see cref="PollingTrustDistributor"/>) does so on a background task whose catch-all logs and
/// continues. When this check lived only in the factory, a strict-mode rejection was therefore
/// swallowed into a startup log line and the server came up half-alive: the web UI worked while
/// every deploy and health check failed with an Autofac resolution exception — observed in
/// production on 1.9.5. An <c>IStartable</c> runs synchronously inside <c>Build()</c>, so strict
/// mode now refuses to start with the guard's own message, and the default Warn lands once in
/// the startup log where operators actually look.</para>
///
/// <para><b>Deliberately narrower than the factory's checks</b>: a missing or unloadable
/// identity is skipped here, not failed — those cases keep today's lazy behaviour (the factory
/// fails with actionable context when Halibut is first used), so this check can never turn a
/// server that used to start into one that does not.</para>
/// </summary>
public sealed class SelfCertStartupCheck : IStartable
{
    private readonly ILifetimeScope _scope;

    public SelfCertStartupCheck(ILifetimeScope scope)
    {
        _scope = scope;
    }

    public void Start()
    {
        if (!_scope.TryResolve<SelfCertSetting>(out var setting)) return;
        if (string.IsNullOrWhiteSpace(setting?.Base64)) return;

        X509Certificate2 serverCert;

        try
        {
            var certBytes = Convert.FromBase64String(setting.Base64);
            serverCert = X509CertificateLoader.LoadPkcs12(certBytes, setting.Password, X509KeyStorageFlags.MachineKeySet);
        }
        catch
        {
            // Unloadable identity: not this check's concern. The HalibutRuntime factory
            // surfaces it with loader context when Halibut is first used, exactly as before.
            return;
        }

        using (serverCert)
            SelfCertValidator.EnsureNotPublishedIdentity(serverCert, SelfCertValidator.ResolveMode());
    }
}

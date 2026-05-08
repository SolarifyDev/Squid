using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Core;
using Squid.Tentacle.Instance;
using Serilog;

namespace Squid.Tentacle.Commands;

/// <summary>
/// <c>squid-tentacle new-certificate [--instance NAME] [--force]</c>
///
/// <para><b>Default semantic (no --force)</b>: load-or-create. If a cert
/// already exists at the resolved path, the same cert is loaded and its
/// thumbprint printed. If none exists, a new self-signed cert is
/// generated and written. Idempotent — fleet automation re-running this
/// command gets stable thumbprints (server's trust list stays valid).</para>
///
/// <para><b>--force semantic</b>: cert is DELETED first, then a new one
/// generated. Used for cert rotation when the existing cert is near
/// expiry (show-config's "expires in N days ⚠️" warning recommends this
/// flow). After rotation, the operator MUST re-register so the server's
/// trust list picks up the new thumbprint — without re-register, polling
/// will fail. The command logs a clear reminder of this requirement.</para>
///
/// <para><b>CertsPath fallback</b>: if <see cref="Configuration.TentacleSettings.CertsPath"/>
/// is empty (e.g. command invoked before <c>register</c> persisted it),
/// falls back to <see cref="InstanceSelector.ResolveCertsPath"/>. Without
/// this fallback the cert manager's <c>EnsureDirectoryExists</c> crashes
/// with <c>System.ArgumentException: path empty</c> — a hostile error
/// message for an operator who just ran <c>create-instance + new-certificate</c>
/// in sequence (the documented flow before re-registering).</para>
/// </summary>
public sealed class NewCertificateCommand : ITentacleCommand
{
    public string Name => "new-certificate";
    public string Description => "Ensure a Tentacle certificate exists (or rotate it with --force)";

    public Task<int> ExecuteAsync(string[] args, IConfiguration config, CancellationToken ct)
    {
        var force = HasForceFlag(args);

        var settings = TentacleApp.LoadTentacleSettings(config);

        // CertsPath fallback: if empty, resolve via InstanceSelector. This
        // covers the standalone-use case (operator runs `create-instance Foo`
        // then `new-certificate --instance Foo` BEFORE register has had a
        // chance to persist Tentacle:CertsPath into the per-instance config).
        // Without this, TentacleCertificateManager.EnsureDirectoryExists
        // crashes with a stack trace — confusing operator UX.
        var certsPath = settings.CertsPath;
        if (string.IsNullOrWhiteSpace(certsPath))
        {
            var (instanceName, _) = InstanceSelector.ExtractInstanceArg(args);
            try
            {
                var instance = InstanceSelector.Resolve(instanceName);
                certsPath = InstanceSelector.ResolveCertsPath(instance);
                Log.Information("CertsPath not in config; resolved via InstanceSelector to {Path}", certsPath);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"Error: cannot resolve CertsPath. {ex.Message}");
                Console.Error.WriteLine("Run 'squid-tentacle create-instance --instance NAME' first, then 'squid-tentacle new-certificate --instance NAME'.");
                return Task.FromResult(1);
            }
        }

        var certManager = new TentacleCertificateManager(certsPath);

        // --force: delete the existing cert file BEFORE LoadOrCreateCertificate
        // so the load-or-create call generates a fresh one. The cert dir
        // itself + sibling files (subscription-id, .pwd) are preserved —
        // only the cert file gets rotated. This way subscription identity
        // (which the server uses to correlate poll connections) stays
        // stable while the cert (TLS material) rotates.
        if (force)
        {
            DeleteExistingCertFile(certsPath);
            Log.Warning("--force passed: existing certificate deleted; new one will be generated");
        }

        var cert = certManager.LoadOrCreateCertificate();
        var subscriptionId = certManager.LoadOrCreateSubscriptionId(settings.SubscriptionId);

        Console.WriteLine($"Thumbprint:     {cert.Thumbprint}");
        Console.WriteLine($"SubscriptionId: {subscriptionId}");
        Console.WriteLine($"CertsPath:      {certsPath}");

        if (force)
        {
            Console.WriteLine();
            Console.WriteLine("⚠️  Certificate rotated. The Squid server's trust list still has the OLD thumbprint.");
            Console.WriteLine("   You MUST re-register to update the server-side trust list:");
            Console.WriteLine();
            Console.WriteLine("     squid-tentacle register --force --server <URL> --api-key <KEY> ...");
            Console.WriteLine();
            Console.WriteLine("   Without this, the agent's next polling attempt will fail TLS validation.");
        }

        return Task.FromResult(0);
    }

    /// <summary>
    /// Detects (without stripping) the <c>--force</c> flag. Doesn't need
    /// to strip because no downstream code reads <c>args</c> for config
    /// keys — just the cert manager which doesn't take args.
    /// </summary>
    private static bool HasForceFlag(string[] args)
    {
        if (args == null) return false;
        foreach (var a in args)
            if (a.Equals("--force", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Deletes the cert file (best-effort) so the next
    /// <see cref="TentacleCertificateManager.LoadOrCreateCertificate"/> call
    /// goes through the create path. The companion .pwd file is also
    /// removed (encrypted-password mode); subscription-id is preserved
    /// (it's the agent's stable identifier, not the cert).
    /// </summary>
    private static void DeleteExistingCertFile(string certsPath)
    {
        try
        {
            var certFile = Path.Combine(certsPath, "tentacle-cert.pfx");
            var pwdFile = certFile + ".pwd";
            if (File.Exists(certFile)) File.Delete(certFile);
            if (File.Exists(pwdFile)) File.Delete(pwdFile);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not delete existing cert file before rotation; LoadOrCreateCertificate may load the old cert instead of generating new");
        }
    }
}

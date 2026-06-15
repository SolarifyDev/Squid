using Serilog;
using Squid.Tentacle.Security;

namespace Squid.Tentacle.Certificate;

/// <summary>
/// Single construction site for the PRODUCTION Tentacle certificate manager. Wires the
/// machine-key encryptor so the certificate password (which protects the PFX holding the
/// agent's PRIVATE KEY) and the polling subscription id are encrypted at rest, instead of
/// the plaintext the bare <see cref="TentacleCertificateManager(string)"/> constructor leaves.
///
/// <para>Every production caller — the runtime (<c>TentacleApp</c>) AND every CLI command
/// (<c>register</c>, <c>show-thumbprint</c>, <c>show-config</c>, <c>check-services</c>,
/// <c>new-certificate</c>) — MUST construct through here. The write path (<c>register</c> /
/// <c>new-certificate</c>) and the read paths (<c>show-thumbprint</c>, the runtime load) have
/// to AGREE on whether the cert password is encrypted: a writer that stores a random,
/// encrypted password and a reader built without the encryptor would fall back to the legacy
/// fixed password and fail to open the PFX. Routing all sites through one factory is what keeps
/// them consistent.</para>
///
/// <para>Non-breaking: <see cref="TentacleCertificateManager"/> already migrates a legacy
/// plaintext install on first encryptor-aware load (writes the existing password / subscription
/// id back in encrypted form), so an upgrade needs no data migration. If the machine-key
/// encryptor cannot be initialised (key derivation throws on an unusual host), this degrades to
/// the legacy plaintext manager rather than preventing the Tentacle from starting — at-rest
/// encryption is best-effort hardening, never a startup gate.</para>
///
/// <para>Threat boundary: the machine-key scheme defends secrets in artefacts that leave the
/// host — backups, disk images, exported logs — where the machine-id is not present. It does NOT
/// defend against an attacker with live read access to this host's filesystem, who can re-derive
/// the key from the (world-readable) machine-id. That is the same trust model as the on-disk PFX
/// itself, so it is the intended scope, not a gap.</para>
/// </summary>
public static class ProductionTentacleCertificateManager
{
    public static ITentacleCertificateManager Create(string certsPath)
        => new TentacleCertificateManager(certsPath, TryCreateMachineKeyEncryptor());

    private static IMachineKeyEncryptor TryCreateMachineKeyEncryptor()
    {
        try
        {
            return new MachineIdKeyEncryptor();
        }
        catch (Exception ex)
        {
            // Defensive: MachineIdProvider.Read() never returns empty (it has a hostname
            // fallback), so MachineIdKeyEncryptor's ctor does not throw under the current
            // contract — this catch is belt-and-suspenders against a future provider/KDF
            // change, ensuring at-rest encryption can never become a Tentacle startup gate.
            Log.Warning(ex, "Could not initialise the machine-key encryptor; Tentacle secrets (certificate password, subscription id) will be stored UNENCRYPTED at rest. Investigate machine-id availability on this host.");
            return null;
        }
    }
}

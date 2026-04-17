namespace Squid.Tentacle.Security;

/// <summary>
/// Protects Tentacle secrets (subscription id, certificate password, stored
/// API keys) at rest on the agent host. Different OSs expose different
/// trust-rooted key material — Linux derives from <c>/etc/machine-id</c>,
/// Windows uses DPAPI LocalMachine scope. A dev/test fallback uses a
/// process-unique key so tests never touch real system keys.
///
/// Encrypted payloads are self-describing and format-versioned so we can
/// rotate the derivation strategy without a data migration.
/// </summary>
public interface IMachineKeyEncryptor
{
    string Protect(string plaintext);

    string Unprotect(string cipherText);
}

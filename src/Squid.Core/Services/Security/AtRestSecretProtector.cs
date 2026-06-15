namespace Squid.Core.Services.Security;

/// <summary>
/// Single source of truth for the per-field at-rest secret contract that every DataProvider
/// seam (account credentials, feed password, certificate password + data, SSH proxy password)
/// applies before persisting / after loading a sensitive string column or JSON field.
///
/// <para>Two halves, both non-breaking by construction:</para>
/// <list type="bullet">
///   <item><b>Protect</b> — encrypt idempotently: an already-encrypted envelope (or a null/empty
///         value) is returned unchanged, so a re-save never double-wraps.</item>
///   <item><b>Unprotect</b> — decrypt read-both: a legacy plaintext (unprefixed) value is returned
///         verbatim, so reads work before AND after migration with no DB migration.</item>
/// </list>
///
/// <para>The sync/async split is intentional and mirrors the wrapped service: <c>Protect</c> is
/// synchronous because <see cref="IVariableEncryptionService.EncryptAsync"/> is synchronous despite
/// its name (a pre-existing misnomer — it returns <c>string</c>, not a Task), while
/// <c>UnprotectAsync</c> is genuinely async because <see cref="IVariableEncryptionService.DecryptAsync"/>
/// is. Do NOT "normalize" the two halves — making <c>Protect</c> async adds a needless Task allocation
/// and making <c>UnprotectAsync</c> sync would block on a real async decrypt.</para>
///
/// <para>This wraps <see cref="IVariableEncryptionService"/> (the AES-256-GCM V2 envelope + KDF) so
/// the providers depend on this narrow contract rather than re-implementing the
/// <c>IsValidEncryptedValue</c>-guarded encrypt + passthrough-decrypt in each one (previously four
/// near-identical copies). The ENTITY-level anti-flush concern (read via <c>QueryNoTracking</c> or
/// <c>Detach</c> before the in-place decrypt) stays in each provider — it is entity-specific and
/// orthogonal to this value transform.</para>
/// </summary>
public interface IAtRestSecretProtector : IScopedDependency
{
    /// <summary>Encrypt <paramref name="value"/> for at-rest storage, idempotently. Null / empty /
    /// already-encrypted input is returned unchanged. <paramref name="kdfScope"/> is the per-concern
    /// KDF salt scope (the entity's encryption scope constant).</summary>
    string Protect(string value, int kdfScope);

    /// <summary>Decrypt an at-rest <paramref name="value"/>, read-both: a legacy unprefixed plaintext
    /// is returned verbatim. <paramref name="kdfScope"/> must match the scope used to Protect it.</summary>
    Task<string> UnprotectAsync(string value, int kdfScope);
}

public sealed class AtRestSecretProtector(IVariableEncryptionService encryption) : IAtRestSecretProtector
{
    public string Protect(string value, int kdfScope)
    {
        // Guard null/empty AND already-an-envelope so a re-save (or a value a previous save already
        // encrypted) is never double-wrapped. EncryptAsync also short-circuits empty, but the explicit
        // guard keeps IsValidEncryptedValue off a null and documents the idempotency contract here.
        if (string.IsNullOrEmpty(value) || encryption.IsValidEncryptedValue(value)) return value;

        return encryption.EncryptAsync(value, kdfScope);
    }

    public Task<string> UnprotectAsync(string value, int kdfScope)
        // DecryptAsync is read-both: an unprefixed plaintext value passes through verbatim.
        => encryption.DecryptAsync(value, kdfScope);
}

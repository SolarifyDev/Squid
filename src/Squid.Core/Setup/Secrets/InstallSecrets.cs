using Squid.Core.Settings.SelfCert;

namespace Squid.Core.Setup.Secrets;

/// <summary>
/// Fills in the secrets a self-contained install must never share with anyone else — the JWT
/// signing key, the variable-encryption master key and the server certificate — when (and only
/// when) the configuration does not already carry them. Returns the configuration keys to persist;
/// an install that already has every secret gets an empty result and nothing is touched.
/// </summary>
public static class InstallSecrets
{
    public const string JwtKeyPath = "Authentication:Jwt:SymmetricKey";
    public const string MasterKeyPath = "Security:VariableEncryption:MasterKey";
    public const string SelfCertBase64Path = "SelfCert:Base64";
    public const string SelfCertPasswordPath = "SelfCert:Password";

    private const int JwtKeyBytes = 64;
    private const int MasterKeyBytes = 32;

    public static IReadOnlyDictionary<string, string> GenerateMissing(IConfiguration configuration)
    {
        var missing = new Dictionary<string, string>();

        AddIfMissing(configuration, missing, JwtKeyPath, () => RandomSecret.Base64(JwtKeyBytes));
        AddIfMissing(configuration, missing, MasterKeyPath, () => RandomSecret.Base64(MasterKeyBytes));
        AddServerCertificateIfMissing(configuration, missing);

        return missing;
    }

    private static void AddIfMissing(IConfiguration configuration, Dictionary<string, string> missing, string key, Func<string> generate)
    {
        if (!string.IsNullOrWhiteSpace(configuration[key])) return;

        missing[key] = generate();
    }

    private static void AddServerCertificateIfMissing(IConfiguration configuration, Dictionary<string, string> missing)
    {
        if (!string.IsNullOrWhiteSpace(new SelfCertSetting(configuration).Base64)) return;

        var certificate = ServerCertificateFactory.Create();

        missing[SelfCertBase64Path] = certificate.Base64;
        missing[SelfCertPasswordPath] = certificate.Password;

        Log.Information("Generated a new server certificate for this install (thumbprint {Thumbprint})", certificate.Thumbprint);
    }
}

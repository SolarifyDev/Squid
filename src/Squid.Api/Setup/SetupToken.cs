using System.Security.Cryptography;
using System.Text;
using Squid.Core.Settings.Setup;
using Squid.Core.Setup;
using Squid.Core.Setup.Secrets;

namespace Squid.Api.Setup;

/// <summary>
/// One-time secret that guards the setup endpoints, so nobody else who can reach the port can
/// claim a fresh install first. Kept in the data directory (owner-only) so it survives a container
/// restart during setup and the install script can read it; never written to the log; deleted once setup completes.
/// </summary>
public sealed class SetupToken
{
    public const string FileName = "setup-token";
    public const string HeaderName = "X-Setup-Token";

    private readonly byte[] _expected;

    private SetupToken(string value, string filePath)
    {
        Value = value;
        FilePath = filePath;
        _expected = Encoding.UTF8.GetBytes(value);
    }

    public string Value { get; }

    public string FilePath { get; }

    public static SetupToken LoadOrCreate(DataDirectorySetting dataDirectory)
    {
        var path = dataDirectory.PathOf(FileName);
        var existing = File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;

        if (existing.Length > 0) return new SetupToken(existing, path);

        var token = RandomSecret.Alphanumeric(32);

        OwnerOnlyFile.WriteAtomically(path, token);

        return new SetupToken(token, path);
    }

    public bool Matches(string candidate) => !string.IsNullOrEmpty(candidate) && CryptographicOperations.FixedTimeEquals(_expected, Encoding.UTF8.GetBytes(candidate));

    public void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}

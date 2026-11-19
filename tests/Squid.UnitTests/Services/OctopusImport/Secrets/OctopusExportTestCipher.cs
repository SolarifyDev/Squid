using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Squid.UnitTests.Services.OctopusImport.Secrets;

/// <summary>
/// Independent reference implementation of the Octopus export cipher (AES-128-CBC, PBKDF2-SHA1,
/// salt "Octopuss", 1000 iterations, "base64(ct)|base64(iv)"). Deliberately NOT shared with
/// production code so a drift in either side fails the round-trip tests.
/// </summary>
internal static class OctopusExportTestCipher
{
    public static string Encrypt(string plaintext, string password, byte ivSeed = 1)
    {
        var key = Rfc2898DeriveBytes.Pbkdf2(password, Encoding.UTF8.GetBytes("Octopuss"), 1_000, HashAlgorithmName.SHA1, 16);
        var iv = Enumerable.Range(ivSeed, 16).Select(i => (byte)i).ToArray();

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;

        using var encryptor = aes.CreateEncryptor();
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = encryptor.TransformFinalBlock(bytes, 0, bytes.Length);

        return $"{Convert.ToBase64String(ciphertext)}|{Convert.ToBase64String(iv)}";
    }

    public static string EncryptRawBytes(byte[] plaintext, string password)
    {
        var key = Rfc2898DeriveBytes.Pbkdf2(password, Encoding.UTF8.GetBytes("Octopuss"), 1_000, HashAlgorithmName.SHA1, 16);
        var iv = new byte[16];

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;

        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

        return $"{Convert.ToBase64String(ciphertext)}|{Convert.ToBase64String(iv)}";
    }
}

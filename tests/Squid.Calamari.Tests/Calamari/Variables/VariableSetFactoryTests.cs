using System.Security.Cryptography;
using System.Text;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Tests.Calamari.Variables;

public class VariableSetFactoryTests
{
    [Fact]
    public void Create_SourcesAreMerged_InOrder()
    {
        var result = VariableSetFactory.Create(
        [
            new InlineVariableSource(("A", "1", false), ("Shared", "base", false)),
            new InlineVariableSource(("B", "2", false), ("Shared", "override", true))
        ]);

        result.Count.ShouldBe(3);
        result.Get("A").ShouldBe("1");
        result.Get("B").ShouldBe("2");
        result.Get("Shared").ShouldBe("override");
        result.Entries.ShouldContain(e => e.Name == "Shared" && e.IsSensitive);
    }

    [Fact]
    public void CreateFromFiles_MissingFiles_ReturnsEmptySet()
    {
        var result = VariableSetFactory.CreateFromFiles(
            "/tmp/does-not-exist-vars.json",
            "/tmp/does-not-exist-sensitive.bin",
            "pw");

        result.Count.ShouldBe(0);
    }

    [Fact]
    public void CreateFromFiles_SensitiveVariablesOverridePlainVariables()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-vars-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var varsPath = Path.Combine(tempDir, "variables.json");
            var sensitivePath = Path.Combine(tempDir, "sensitive.bin");
            const string password = "p@ssw0rd";

            File.WriteAllText(varsPath, "{\"Name\":\"plain\",\"Env\":\"prod\"}");
            var encryptedJson = EncryptSensitiveJson("{\"Name\":\"secret\",\"ApiKey\":\"xyz\"}", password);
            File.WriteAllBytes(sensitivePath, encryptedJson);

            var result = VariableSetFactory.CreateFromFiles(varsPath, sensitivePath, password);

            result.Count.ShouldBe(3);
            result.Get("Name").ShouldBe("secret");
            result.Get("Env").ShouldBe("prod");
            result.Get("ApiKey").ShouldBe("xyz");
            result.Entries.ShouldContain(e => e.Name == "ApiKey" && e.IsSensitive);
            result.Entries.ShouldContain(e => e.Name == "Name" && e.IsSensitive);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static byte[] EncryptSensitiveJson(string plaintext, string password)
    {
        const int iterations = 1000;
        var salt = Encoding.UTF8.GetBytes("SquidDep");
        var ivPrefix = "IV__"u8.ToArray();

        using var keyDerivation = new Rfc2898DeriveBytes(password, salt, iterations);
        var key = keyDerivation.GetBytes(16);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Key = key;
        aes.GenerateIV();

        using var output = new MemoryStream();
        output.Write(ivPrefix, 0, ivPrefix.Length);
        output.Write(aes.IV, 0, aes.IV.Length);

        using (var crypto = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
        using (var writer = new StreamWriter(crypto, Encoding.UTF8))
        {
            writer.Write(plaintext);
        }

        return output.ToArray();
    }

    private sealed class InlineVariableSource : IVariableSource
    {
        private readonly VariableEntry[] _entries;

        public InlineVariableSource(params (string Name, string Value, bool IsSensitive)[] entries)
        {
            _entries = entries
                .Select(e => new VariableEntry(e.Name, e.Value, e.IsSensitive, Source: "inline"))
                .ToArray();
        }

        public string Name => "inline";

        public IEnumerable<VariableEntry> Load() => _entries;
    }
}

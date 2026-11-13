using System.Linq;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Configuration;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Settings.Security;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

public class OctopusRestKeyBindingTests
{
    private const string KeyA = "API-TEST-KEY-A";
    private const string KeyB = "API-TEST-KEY-B";

    private static readonly byte[] MasterKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void HmacPurpose_Pinned()
    {
        // Stored fingerprints are compared across chunks of a live session; renaming the purpose orphans them.
        OctopusRestKeyBinding.HmacPurpose.ShouldBe("octopus-import-api-key");
    }

    [Fact]
    public void Fingerprint_IsTheHmacOfPurposeAndTrimmedKey_NeverTheKey()
    {
        var fingerprint = OctopusRestKeyBinding.Fingerprint(MasterKey, "  " + KeyA + " ");
        var expected = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(MasterKey, Encoding.UTF8.GetBytes("octopus-import-api-key\n" + KeyA))).ToLowerInvariant();

        fingerprint.ShouldBe(expected);
        fingerprint.ShouldNotContain("KEY-A", Case.Insensitive);
    }

    [Fact]
    public void Fingerprint_DependsOnKeyAndMasterKey()
    {
        var other = MasterKey.Select(b => (byte)(b + 1)).ToArray();

        OctopusRestKeyBinding.Fingerprint(MasterKey, KeyA).ShouldNotBe(OctopusRestKeyBinding.Fingerprint(MasterKey, KeyB));
        OctopusRestKeyBinding.Fingerprint(MasterKey, KeyA).ShouldNotBe(OctopusRestKeyBinding.Fingerprint(other, KeyA));
        OctopusRestKeyBinding.Fingerprint(MasterKey, KeyA).ShouldBe(OctopusRestKeyBinding.Fingerprint(MasterKey.ToArray(), KeyA));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("  ", 0)]
    [InlineData("AQIDBA==", 4)]
    [InlineData("not base64!", 11)]
    public void MasterKeyBytes_Base64ElseUtf8ElseEmpty(string configured, int expectedLength)
    {
        OctopusRestKeyBinding.MasterKeyBytes(configured).Length.ShouldBe(expectedLength);
    }

    [Fact]
    public void Bind_FirstChunk_PinsUserAndKey_ReportsChanged()
    {
        var cursor = new OctopusImportHistoryFetchDto();

        OctopusRestKeyBinding.Bind(cursor, "Users-7", Fp(KeyA)).ShouldBeTrue();

        (cursor.UserId, cursor.KeyHmac).ShouldBe(("Users-7", Fp(KeyA)));
    }

    [Theory]
    [InlineData("Users-7", KeyA, false)]
    [InlineData("users-7", KeyA, false)]
    [InlineData("Users-7", KeyB, true)]
    public void Bind_SameUser_AcceptsAndReportsWhetherTheKeyChanged(string userId, string key, bool expectedChanged)
    {
        var cursor = new OctopusImportHistoryFetchDto { UserId = "Users-7", KeyHmac = Fp(KeyA) };

        OctopusRestKeyBinding.Bind(cursor, userId, Fp(key)).ShouldBe(expectedChanged);

        (cursor.UserId, cursor.KeyHmac).ShouldBe(("Users-7", Fp(key)));
    }

    [Fact]
    public void Bind_KeyOfAnotherUser_RefusedAndCursorUnchanged()
    {
        var cursor = new OctopusImportHistoryFetchDto { UserId = "Users-7", KeyHmac = Fp(KeyA) };

        var ex = Should.Throw<OctopusImportHistoryServerMismatchException>(() => OctopusRestKeyBinding.Bind(cursor, "Users-8", Fp(KeyB)));

        ex.PinnedField.ShouldBe(OctopusRestKeyBinding.UserPinnedField);
        (cursor.UserId, cursor.KeyHmac).ShouldBe(("Users-7", Fp(KeyA)));
    }

    // The production factory fingerprints each client's key under the configured master key, so the fetcher can bind without the key.
    [Fact]
    public void Factory_GivesEveryClientTheFingerprintOfItsKeyUnderTheMasterKey()
    {
        var configured = Convert.ToBase64String(MasterKey);
        var security = new SecuritySetting(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Security:VariableEncryption:MasterKey"] = configured }).Build());

        using var client = new OctopusRestClientFactory(security, new OctopusRestOptions()).Create("https://octopus.example.com/", KeyA);

        client.KeyFingerprint.ShouldBe(Fp(KeyA));
    }

    private static string Fp(string key) => OctopusRestKeyBinding.Fingerprint(MasterKey, key);
}

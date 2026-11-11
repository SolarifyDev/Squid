using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;

namespace Squid.UnitTests.Services.OctopusImport.Secrets;

public class OctopusImportExportSecretStoreTests
{
    private readonly Mock<IOctopusImportSessionDataProvider> _dataProvider = new();
    private readonly Mock<ICurrentUser> _currentUser = new();

    public OctopusImportExportSecretStoreTests()
    {
        _currentUser.SetupGet(u => u.Id).Returns(42);
    }

    public static TheoryData<string> AdversarialPasswords => new()
    {
        "SQUID_ENCRYPTED_V2:abc",
        "SQUID_ENCRYPTED_V2:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        "SQUID_ENCRYPTED:abc",
        "SQUID_ENCRYPTED_V2:",
        OctopusImportExportSecretStore.NotRetainedMarker,
        "pässwörd-密码-🔐-ünïcødé",
        "  leading and trailing whitespace  ",
        "\ttab\r\nnewline\0nul",
        "'quoted' \"double\" `back` $dollar #{Octopus.Var} |pipe|",
        new string('x', 4096) + "-end"
    };

    [Fact]
    public void ExportPasswordKdfScope_IsPinnedAndNeverCollidesWithEntityIds()
    {
        // Entity ids (used as KDF scopes for variables) are positive; the export password lives in its own scope.
        OctopusImportExportSecretStore.ExportPasswordKdfScope.ShouldBe(-1001);
    }

    [Fact]
    public void PersistedFormatConstants_ArePinned()
    {
        // Rows written by one server version must stay readable by the next: changing either literal
        // turns every retained export password of an active session into "unreadable".
        OctopusImportExportSecretStore.PurposeTag.ShouldBe("squid.octopus-import.export-password.v1\n");
        OctopusImportExportSecretStore.NotRetainedMarker.ShouldBe("octopus-import:export-password-not-retained");
    }

    [Fact]
    public async Task StoreAsync_PersistsOnlyAnEncryptedEnvelope_AndLoadAsyncDecryptsArchiveValues()
    {
        var session = SetupSession();

        await Store().StoreAsync(session.SessionId, 7, "sjfood-export-pw");
        var secrets = await Store().LoadAsync(session.SessionId, 7);

        session.ProtectedExportPassword.ShouldNotBeNullOrEmpty();
        session.ProtectedExportPassword.ShouldNotContain("sjfood-export-pw");
        secrets.TryDecrypt(OctopusExportTestCipher.Encrypt("db-secret", "sjfood-export-pw"), out var plaintext).ShouldBeTrue();
        plaintext.ShouldBe("db-secret");
        _dataProvider.Verify(p => p.UpdateSessionAsync(session, It.IsAny<byte[]>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [MemberData(nameof(AdversarialPasswords))]
    public async Task StoreAsync_EveryPassword_IsEncryptedAtRestAndRoundTripsByteExact(string password)
    {
        var session = SetupSession();

        await Store().StoreAsync(session.SessionId, 7, password);
        var secrets = await Store().LoadAsync(session.SessionId, 7);

        session.ProtectedExportPassword.ShouldStartWith("SQUID_ENCRYPTED_V2:", customMessage: "the export password must never be persisted as typed, whatever it looks like");
        session.ProtectedExportPassword["SQUID_ENCRYPTED_V2:".Length..].ShouldNotContain(password);
        secrets.IsAvailable.ShouldBeTrue();
        secrets.TryDecrypt(OctopusExportTestCipher.Encrypt("archive-value", password), out var plaintext).ShouldBeTrue("the retained password must derive the exact same Octopus key (byte-exact UTF-8 round trip)");
        plaintext.ShouldBe("archive-value");
        secrets.CanDecrypt(OctopusExportTestCipher.Encrypt("archive-value", password + " ")).ShouldBeFalse();
    }

    [Fact]
    public async Task StoreAsync_SamePasswordTwice_ProducesDistinctEnvelopes()
    {
        var first = SetupSession();
        var second = SetupSession();

        await Store().StoreAsync(first.SessionId, 7, "same-password");
        await Store().StoreAsync(second.SessionId, 7, "same-password");

        first.ProtectedExportPassword.ShouldNotBe(second.ProtectedExportPassword);
    }

    [Fact]
    public async Task StoreAsync_EmptyPassword_IsANoOp()
    {
        await Store().StoreAsync(Guid.NewGuid(), 7, string.Empty);

        _dataProvider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StoreAsync_SessionOwnedBySomeoneElse_Throws()
    {
        await Should.ThrowAsync<OctopusImportSessionNotFoundException>(() => Store().StoreAsync(Guid.NewGuid(), 7, "pw"));
    }

    [Fact]
    public void Construction_NeverBuildsTheAtRestEncryptionService()
    {
        // Autofac resolves the store for every import handler; with the committed empty MasterKey (Strict)
        // building the encryption service throws, so it must only be built when a password is stored/loaded.
        var built = false;

        _ = new OctopusImportExportSecretStore(_dataProvider.Object, new Lazy<IVariableEncryptionService>(() => { built = true; return RealEncryption(); }), _currentUser.Object);

        built.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreAsync_AtRestKeyUnavailable_RetainsOnlyTheNotRetainedMarker_AndLoadReportsNotRetained_EvenOnceAKeyIsConfigured(bool keyConfiguredBeforeLoad)
    {
        // The marker can never become readable: no envelope was ever written, so no key (original or new)
        // recovers it. It must load as "not retained" — behaving like no password — and never as "unreadable",
        // which confirmation refuses in the hope that the original key comes back.
        var session = SetupSession();

        await Store(UnavailableEncryption()).StoreAsync(session.SessionId, 7, "sjfood-export-pw");
        var secrets = await Store(keyConfiguredBeforeLoad ? null : UnavailableEncryption()).LoadAsync(session.SessionId, 7);

        session.ProtectedExportPassword.ShouldBe(OctopusImportExportSecretStore.NotRetainedMarker);
        secrets.ShouldBeSameAs(OctopusExportSecrets.NotRetained);
        secrets.IsUnreadable.ShouldBeFalse();
        secrets.IsNotRetained.ShouldBeTrue();
        secrets.IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task LoadAsync_NoRetainedPassword_ReturnsNoneWithoutTouchingTheAtRestKey()
    {
        var session = SetupSession();

        var secrets = await Store(UnavailableEncryption()).LoadAsync(session.SessionId, 7);

        secrets.ShouldBeSameAs(OctopusExportSecrets.None);
    }

    [Fact]
    public async Task LoadAsync_RetainedEnvelopeButAtRestKeyNowUnavailable_IsUnreadable()
    {
        var session = SetupSession();
        await Store().StoreAsync(session.SessionId, 7, "sjfood-export-pw");

        var secrets = await Store(UnavailableEncryption()).LoadAsync(session.SessionId, 7);

        secrets.ShouldBeSameAs(OctopusExportSecrets.Unreadable);
    }

    [Fact]
    public async Task LoadAsync_EnvelopeUnreadableWithCurrentMasterKey_IsUnreadableInsteadOfFailing()
    {
        var session = SetupSession();
        await Store(new Lazy<IVariableEncryptionService>(() => RealEncryption(seed: 99))).StoreAsync(session.SessionId, 7, "pw");

        var secrets = await Store().LoadAsync(session.SessionId, 7);

        secrets.ShouldBeSameAs(OctopusExportSecrets.Unreadable);
    }

    [Fact]
    public void VariableEncryptionV2_IgnoresTheKdfScope_SoTheScopeAloneIsNoDomainSeparation()
    {
        // Characterises the audit claim: a V2 envelope encrypted for a variable set decrypts under the
        // export-password scope, so the store must bind its envelopes to their purpose itself.
        var encryption = RealEncryption();

        var variableEnvelope = encryption.EncryptAsync("variable-value", 5);

        encryption.Decrypt(variableEnvelope, OctopusImportExportSecretStore.ExportPasswordKdfScope).ShouldBe("variable-value");
    }

    [Theory]
    [InlineData("plaintext-password")]
    [InlineData("SQUID_ENCRYPTED:bGVnYWN5")]
    [InlineData("SQUID_ENCRYPTED_V2:not-base64!")]
    [InlineData("SQUID_ENCRYPTED_V2:AAAA")]
    public async Task LoadAsync_ColumnHoldsSomethingOtherThanAStoreEnvelope_IsUnreadableNeverUsedAsThePassword(string storedValue)
    {
        var session = SetupSession();
        session.ProtectedExportPassword = storedValue;

        var secrets = await Store().LoadAsync(session.SessionId, 7);

        secrets.ShouldBeSameAs(OctopusExportSecrets.Unreadable);
        secrets.CanDecrypt(OctopusExportTestCipher.Encrypt("v", storedValue)).ShouldBeFalse();
    }

    [Fact]
    public async Task LoadAsync_EnvelopeOfAnotherPurposeUnderTheSameMasterKey_IsUnreadable()
    {
        // e.g. a sensitive variable's envelope copied into the column: same key, same V2 format, wrong purpose.
        var session = SetupSession();
        session.ProtectedExportPassword = RealEncryption().EncryptAsync("sjfood-export-pw", OctopusImportExportSecretStore.ExportPasswordKdfScope);

        var secrets = await Store().LoadAsync(session.SessionId, 7);

        secrets.ShouldBeSameAs(OctopusExportSecrets.Unreadable);
        secrets.CanDecrypt(OctopusExportTestCipher.Encrypt("v", "sjfood-export-pw")).ShouldBeFalse();
    }

    [Fact]
    public async Task StoreAsync_Cancelled_PropagatesWithoutWritingAnything()
    {
        var session = SetupSession();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _dataProvider.Setup(p => p.GetSessionAsync(session.SessionId, 42, 7, It.Is<CancellationToken>(t => t.IsCancellationRequested))).ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => Store().StoreAsync(session.SessionId, 7, "pw", cts.Token));

        _dataProvider.Verify(p => p.UpdateSessionAsync(It.IsAny<OctopusImportSession>(), It.IsAny<byte[]>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private OctopusImportSession SetupSession()
    {
        var session = Session();
        _dataProvider.Setup(p => p.GetSessionAsync(session.SessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _dataProvider.Setup(p => p.GetSessionNoTrackingAsync(session.SessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        return session;
    }

    private OctopusImportExportSecretStore Store(Lazy<IVariableEncryptionService> encryption = null)
        => new(_dataProvider.Object, encryption ?? new Lazy<IVariableEncryptionService>(() => RealEncryption()), _currentUser.Object);

    // Mirrors what Autofac's Lazy<T> throws when VariableEncryptionService's ctor rejects the configured
    // MasterKey (Strict + empty key): the ctor's InvalidOperationException wrapped in a resolution exception.
    private static Lazy<IVariableEncryptionService> UnavailableEncryption()
        => new(() => throw new Autofac.Core.DependencyResolutionException("An exception was thrown while activating VariableEncryptionService.", new InvalidOperationException("MasterKey is empty or missing.")));

    private static OctopusImportSession Session()
        => new() { Id = 1, SessionId = Guid.NewGuid(), DestinationSpaceId = 7, OwnerUserId = 42, State = "Uploaded", DataVersion = Guid.NewGuid().ToByteArray() };

    private static VariableEncryptionService RealEncryption(byte seed = 1)
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + seed);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["Security:VariableEncryption:MasterKey"] = Convert.ToBase64String(key) })
            .Build();

        return new VariableEncryptionService(new SecuritySetting(config));
    }
}

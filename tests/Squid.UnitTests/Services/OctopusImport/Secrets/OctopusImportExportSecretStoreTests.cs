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
    private readonly IAtRestSecretProtector _protector = RealProtector();

    public OctopusImportExportSecretStoreTests()
    {
        _currentUser.SetupGet(u => u.Id).Returns(42);
    }

    [Fact]
    public void ExportPasswordKdfScope_IsPinnedAndNeverCollidesWithEntityIds()
    {
        // Entity ids (used as KDF scopes for variables) are positive; the export password lives in its own scope.
        OctopusImportExportSecretStore.ExportPasswordKdfScope.ShouldBe(-1001);
    }

    [Fact]
    public async Task StoreAsync_PersistsOnlyAnEncryptedEnvelope_AndLoadAsyncDecryptsArchiveValues()
    {
        var session = Session();
        _dataProvider.Setup(p => p.GetSessionAsync(session.SessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _dataProvider.Setup(p => p.GetSessionNoTrackingAsync(session.SessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        var store = new OctopusImportExportSecretStore(_dataProvider.Object, _protector, _currentUser.Object);

        await store.StoreAsync(session.SessionId, 7, "sjfood-export-pw");
        var secrets = await store.LoadAsync(session.SessionId, 7);

        session.ProtectedExportPassword.ShouldNotBeNullOrEmpty();
        session.ProtectedExportPassword.ShouldNotContain("sjfood-export-pw");
        secrets.TryDecrypt(OctopusExportTestCipher.Encrypt("db-secret", "sjfood-export-pw"), out var plaintext).ShouldBeTrue();
        plaintext.ShouldBe("db-secret");
        _dataProvider.Verify(p => p.UpdateSessionAsync(session, It.IsAny<byte[]>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StoreAsync_EmptyPassword_IsANoOp()
    {
        var store = new OctopusImportExportSecretStore(_dataProvider.Object, _protector, _currentUser.Object);

        await store.StoreAsync(Guid.NewGuid(), 7, string.Empty);

        _dataProvider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StoreAsync_SessionOwnedBySomeoneElse_Throws()
    {
        var store = new OctopusImportExportSecretStore(_dataProvider.Object, _protector, _currentUser.Object);

        await Should.ThrowAsync<OctopusImportSessionNotFoundException>(() => store.StoreAsync(Guid.NewGuid(), 7, "pw"));
    }

    [Fact]
    public async Task LoadAsync_NoRetainedPassword_ReturnsNone()
    {
        var session = Session();
        _dataProvider.Setup(p => p.GetSessionNoTrackingAsync(session.SessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var secrets = await new OctopusImportExportSecretStore(_dataProvider.Object, _protector, _currentUser.Object).LoadAsync(session.SessionId, 7);

        secrets.ShouldBeSameAs(OctopusExportSecrets.None);
    }

    [Fact]
    public async Task LoadAsync_EnvelopeUnreadableWithCurrentMasterKey_FallsBackToNoneInsteadOfFailing()
    {
        var session = Session();
        session.ProtectedExportPassword = RealProtector(seed: 99).Protect("pw", OctopusImportExportSecretStore.ExportPasswordKdfScope);
        _dataProvider.Setup(p => p.GetSessionNoTrackingAsync(session.SessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var secrets = await new OctopusImportExportSecretStore(_dataProvider.Object, _protector, _currentUser.Object).LoadAsync(session.SessionId, 7);

        secrets.ShouldBeSameAs(OctopusExportSecrets.None);
    }

    private static OctopusImportSession Session()
        => new() { Id = 1, SessionId = Guid.NewGuid(), DestinationSpaceId = 7, OwnerUserId = 42, State = "Uploaded", DataVersion = Guid.NewGuid().ToByteArray() };

    private static IAtRestSecretProtector RealProtector(byte seed = 1)
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + seed);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["Security:VariableEncryption:MasterKey"] = Convert.ToBase64String(key) })
            .Build();

        return new AtRestSecretProtector(new VariableEncryptionService(new SecuritySetting(config)));
    }
}

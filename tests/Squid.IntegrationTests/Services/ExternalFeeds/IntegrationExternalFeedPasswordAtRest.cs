using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;

namespace Squid.IntegrationTests.Services.ExternalFeeds;

/// <summary>
/// End-to-end (real Postgres + real EF + real <see cref="Squid.Core.Services.Security.IVariableEncryptionService"/>
/// with the test MasterKey) coverage of at-rest encryption for
/// <see cref="ExternalFeed.Password"/> through the real <see cref="IExternalFeedDataProvider"/>
/// seam: a written feed's RAW column carries the <c>SQUID_ENCRYPTED_V2:</c> envelope and never the
/// cleartext password; reads decrypt transparently; a pre-existing PLAINTEXT row still reads back
/// (read-both / zero-migration); and — the regression that bit the account slice — a read+decrypt
/// followed by an unrelated SaveChanges in the SAME scope must NOT flush plaintext back (reads are
/// AsNoTracking).
/// </summary>
public class IntegrationExternalFeedPasswordAtRest : TestBase
{
    private const string Secret = "feed-secret-pat-4f1c-VALUE";

    public IntegrationExternalFeedPasswordAtRest()
        : base("ExternalFeedPasswordAtRest", "squid_it_feed_pw_atrest")
    {
    }

    [Fact]
    public async Task Create_StoresPasswordEncrypted_AndReadsBackDecrypted()
    {
        var id = await Run<IExternalFeedDataProvider, int>(async provider =>
        {
            var feed = NewFeed(Secret);
            await provider.AddExternalFeedAsync(feed).ConfigureAwait(false);
            return feed.Id;
        }).ConfigureAwait(false);

        await Run<IRepository>(async repository =>
        {
            var raw = await repository.Query<ExternalFeed>(f => f.Id == id).FirstOrDefaultAsync().ConfigureAwait(false);

            raw.ShouldNotBeNull();
            raw.Password.ShouldStartWith("SQUID_ENCRYPTED_V2:", customMessage: "the persisted feed password must be a V2 envelope, never cleartext");
            raw.Password.ShouldNotContain(Secret, customMessage: "the secret must not survive verbatim in the DB column");
        }).ConfigureAwait(false);

        await Run<IExternalFeedDataProvider>(async provider =>
        {
            var loaded = await provider.GetFeedByIdAsync(id).ConfigureAwait(false);
            loaded.Password.ShouldBe(Secret, customMessage: "the provider must decrypt on read");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task LegacyPlaintextRow_ReadsBack_NonBreaking()
    {
        var id = 0;
        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var feed = NewFeed(Secret);   // inserted raw (bypassing the encrypting provider) = legacy plaintext
            await repository.InsertAsync(feed).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
            id = feed.Id;
        }).ConfigureAwait(false);

        await Run<IExternalFeedDataProvider>(async provider =>
        {
            var loaded = await provider.GetFeedByIdAsync(id).ConfigureAwait(false);
            loaded.Password.ShouldBe(Secret, customMessage: "read-both: a pre-existing plaintext row must read back verbatim");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task ReadThenUnrelatedSaveInSameScope_DoesNotFlushPlaintextBack()
    {
        var id = await Run<IExternalFeedDataProvider, int>(async provider =>
        {
            var feed = NewFeed(Secret);
            await provider.AddExternalFeedAsync(feed).ConfigureAwait(false);
            return feed.Id;
        }).ConfigureAwait(false);

        await Run<IExternalFeedDataProvider, IUnitOfWork>(async (provider, unitOfWork) =>
        {
            var loaded = await provider.GetFeedByIdAsync(id).ConfigureAwait(false);
            loaded.Password.ShouldBe(Secret);

            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);   // would flush plaintext if the read tracked
        }).ConfigureAwait(false);

        await Run<IRepository>(async repository =>
        {
            var raw = await repository.Query<ExternalFeed>(f => f.Id == id).FirstOrDefaultAsync().ConfigureAwait(false);
            raw.Password.ShouldStartWith("SQUID_ENCRYPTED_V2:",
                customMessage: "a read+decrypt then unrelated same-scope SaveChanges must NOT un-encrypt the column — reads must be AsNoTracking");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Update_ReEncryptsPassword()
    {
        const string rotated = "feed-secret-rotated-8a2d-VALUE";

        var id = await Run<IExternalFeedDataProvider, int>(async provider =>
        {
            var feed = NewFeed(Secret);
            await provider.AddExternalFeedAsync(feed).ConfigureAwait(false);
            return feed.Id;
        }).ConfigureAwait(false);

        await Run<IExternalFeedDataProvider>(async provider =>
        {
            var loaded = await provider.GetFeedByIdAsync(id).ConfigureAwait(false);   // decrypted
            loaded.Password = rotated;                                                 // plaintext rotation
            await provider.UpdateExternalFeedAsync(loaded).ConfigureAwait(false);      // re-encrypts
        }).ConfigureAwait(false);

        await Run<IRepository>(async repository =>
        {
            var raw = await repository.Query<ExternalFeed>(f => f.Id == id).FirstOrDefaultAsync().ConfigureAwait(false);
            raw.Password.ShouldStartWith("SQUID_ENCRYPTED_V2:");
            raw.Password.ShouldNotContain(rotated);
        }).ConfigureAwait(false);

        await Run<IExternalFeedDataProvider>(async provider =>
        {
            var loaded = await provider.GetFeedByIdAsync(id).ConfigureAwait(false);
            loaded.Password.ShouldBe(rotated, customMessage: "the rotated secret must decrypt back after update");
        }).ConfigureAwait(false);
    }

    private static ExternalFeed NewFeed(string password) => new()
    {
        SpaceId = 1,
        Name = $"at-rest-feed-{Guid.NewGuid():N}"[..24],
        Slug = $"feed-{Guid.NewGuid():N}",
        FeedType = "Docker",
        FeedUri = "https://registry.example.com",
        Username = "svc",
        Password = password
    };
}

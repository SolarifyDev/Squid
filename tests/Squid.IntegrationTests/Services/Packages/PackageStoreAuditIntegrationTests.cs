using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Events;
using Squid.Core.Services.Packages.Storage;
using Squid.Message.Commands.PackageStorage;
using Squid.Message.Enums.Events;
using Squid.Message.Models.PackageStorage;

namespace Squid.IntegrationTests.Services.Packages;

/// <summary>
/// Package stores hold credentials, so changing one must leave an audit event, and the event must say what changed without ever carrying the
/// settings or the secret. Real Postgres + the real SaveChanges document-audit path.
/// </summary>
public class PackageStoreAuditIntegrationTests : TestBase
{
    private const string Token = "audit-token-77c1-PLAINTEXT-VALUE";
    private const string SecretKind = "AuditSecretKind";

    public PackageStoreAuditIntegrationTests() : base("PackageStoreAudit", "squid_it_package_store_audit", builder => builder.RegisterType<AuditSecretProvider>().As<IPackageBlobStoreProvider>().SingleInstance())
    {
    }

    private Task<PackageStoreDto> CreateAsync(string name = "Audited") => Run<IPackageStoreService, PackageStoreDto>(service => service.CreateAsync(new CreatePackageStoreCommand
    {
        Name = name, Kind = SecretKind,
        Properties = new Dictionary<string, string> { ["Endpoint"] = "https://audit.example" },
        Secrets = new Dictionary<string, string> { ["Token"] = Token }
    }, CancellationToken.None));

    private Task<List<Event>> EventsAsync() => Run<IRepository, List<Event>>(repository => repository.QueryNoTracking<Event>().OrderBy(e => e.Id).ToListAsync());

    private static int Count(List<Event> events, EventCategory category) => events.Count(e => e.Category == category);

    [Fact]
    public async Task Create_RecordsDocumentCreated_NamingTheStore_UnderTheSystemSpace()
    {
        await CreateAsync("Audited");

        var created = (await EventsAsync()).Where(e => e.Category == EventCategory.DocumentCreated).ShouldHaveSingleItem();

        created.ReferencesJson.ShouldContain("PackageStore");
        created.ReferencesJson.ShouldContain("Audited");
        created.SpaceId.ShouldBe(0);
    }

    [Fact]
    public async Task Update_RecordsDocumentModified()
    {
        var dto = await CreateAsync();

        await Run<IPackageStoreService>(service => service.UpdateAsync(new UpdatePackageStoreCommand { Id = dto.Id, Name = "Renamed", Properties = new Dictionary<string, string> { ["Endpoint"] = "https://other" } }, CancellationToken.None));

        var modified = (await EventsAsync()).Where(e => e.Category == EventCategory.DocumentModified).ShouldHaveSingleItem();

        modified.ReferencesJson.ShouldContain("Renamed");
    }

    [Fact]
    public async Task Delete_RecordsDocumentDeleted_AndTheRowIsGone()
    {
        await Run<IPackageStoreDataProvider>(provider => provider.AddAsync(new Squid.Core.Persistence.Entities.PackageStorage.PackageStore { Name = "Default", Kind = "FileSystem", IsDefault = true }, CancellationToken.None));
        var dto = await CreateAsync("Doomed");

        await Run<IPackageStoreService>(service => service.DeleteAsync(dto.Id, CancellationToken.None));

        var deleted = (await EventsAsync()).Where(e => e.Category == EventCategory.DocumentDeleted).ShouldHaveSingleItem();

        deleted.ReferencesJson.ShouldContain("Doomed");
        (await Run<IRepository, int>(repository => repository.QueryNoTracking<Squid.Core.Persistence.Entities.PackageStorage.PackageStore>().CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task RejectedDelete_OfTheDefault_RecordsNothing_AndKeepsTheRow()
    {
        var id = 0;

        await Run<IPackageStoreDataProvider>(async provider =>
        {
            var store = new Squid.Core.Persistence.Entities.PackageStorage.PackageStore { Name = "Default", Kind = "FileSystem", IsDefault = true };
            await provider.AddAsync(store, CancellationToken.None);
            id = store.Id;
        });

        (await Run<IPackageStoreDataProvider, bool>(provider => provider.DeleteIfNotDefaultAsync(id, CancellationToken.None))).ShouldBeFalse();

        Count(await EventsAsync(), EventCategory.DocumentDeleted).ShouldBe(0);
        (await Run<IRepository, int>(repository => repository.QueryNoTracking<Squid.Core.Persistence.Entities.PackageStorage.PackageStore>().CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task NoEvent_EverCarriesThePlaintextOrTheCiphertextSecret()
    {
        var dto = await CreateAsync();

        var ciphertext = await Run<IRepository, string>(async repository => (await repository.QueryNoTracking<Squid.Core.Persistence.Entities.PackageStorage.PackageStore>(s => s.Id == dto.Id).SingleAsync()).Secret);

        ciphertext.ShouldStartWith("SQUID_ENCRYPTED_V2:");

        await Run<IPackageStoreService>(service => service.UpdateAsync(new UpdatePackageStoreCommand { Id = dto.Id, Name = "Audited", Properties = new Dictionary<string, string> { ["Endpoint"] = "https://other" }, Secrets = new Dictionary<string, string> { ["Token"] = "rotated-PLAINTEXT" } }, CancellationToken.None));
        await Run<IPackageStoreService>(service => service.DeleteAsync(dto.Id, CancellationToken.None));

        var events = await EventsAsync();
        var everything = JsonSerializer.Serialize(events);

        events.Select(e => e.Category).ShouldBe([EventCategory.DocumentCreated, EventCategory.DocumentModified, EventCategory.DocumentDeleted], "the test must have produced the events it then inspects");
        everything.ShouldNotContain(Token);
        everything.ShouldNotContain("rotated-PLAINTEXT");
        everything.ShouldNotContain("SQUID_ENCRYPTED");
        everything.ShouldNotContain(ciphertext);
        everything.ShouldNotContain("audit.example");
    }

    [Fact]
    public async Task RecordingATestResult_ProducesNoEvent()
    {
        var dto = await CreateAsync();
        var before = (await EventsAsync()).Count;

        await Run<IPackageStoreService>(service => service.TestAsync(dto.Id, CancellationToken.None));

        (await EventsAsync()).Count.ShouldBe(before);
        (await Run<IPackageStoreService, PackageStoreDto>(service => service.GetAsync(dto.Id, CancellationToken.None))).LastTestSucceeded.ShouldBe(true);
    }

    [Fact]
    public async Task SetDefault_IsAudited_AsAModificationOfEachStoreItChanges()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");

        await Run<IPackageStoreService>(service => service.SetDefaultAsync(a.Id, CancellationToken.None));
        var before = Count(await EventsAsync(), EventCategory.DocumentModified);

        await Run<IPackageStoreService>(service => service.SetDefaultAsync(b.Id, CancellationToken.None));

        (Count(await EventsAsync(), EventCategory.DocumentModified) - before).ShouldBe(2);
    }

    public sealed class AuditSecretProvider : IPackageBlobStoreProvider
    {
        public string Kind => SecretKind;

        public PackageStoreProviderDescriptor Describe() => new()
        {
            Kind = Kind, DisplayName = "Audit secret", Description = "test",
            Fields =
            [
                new PackageStoreFieldDescriptor { Key = "Endpoint", Label = "Endpoint", Kind = Squid.Message.Enums.PackageStorage.PackageStoreFieldKind.Text, Required = true },
                new PackageStoreFieldDescriptor { Key = "Token", Label = "Token", Kind = Squid.Message.Enums.PackageStorage.PackageStoreFieldKind.Secret, Required = true }
            ]
        };

        public IPackageBlobStore Create(PackageBlobStoreConfiguration configuration) => throw new NotSupportedException();

        public Task<PackageBlobStoreTestResult> TestAsync(PackageBlobStoreConfiguration configuration, CancellationToken ct) => Task.FromResult(new PackageBlobStoreTestResult { Success = true, Message = "ok" });
    }
}

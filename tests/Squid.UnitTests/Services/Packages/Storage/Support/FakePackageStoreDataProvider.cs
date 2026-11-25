using System.IO;
using System.Linq;
using Squid.Core.Persistence.Entities.PackageStorage;
using Squid.Core.Services.Packages.Storage;

namespace Squid.UnitTests.Services.Packages.Storage.Support;

/// <summary>An in-memory table: stores what it is given and hands back copies, so a caller can only change a row by writing it back.</summary>
internal sealed class FakePackageStoreDataProvider : IPackageStoreDataProvider
{
    private readonly List<PackageStore> _rows = new();
    private int _nextId = 1;

    public int Reads { get; private set; }

    public IReadOnlyList<PackageStore> Rows => _rows;

    public PackageStore Seed(string name, string kind, string properties = "{}", string secret = null, bool isDefault = false)
    {
        var row = new PackageStore { Id = _nextId++, Name = name, Kind = kind, Properties = properties, Secret = secret, IsDefault = isDefault, LastModifiedDate = DateTimeOffset.UtcNow };

        _rows.Add(row);

        return Clone(row);
    }

    public Task<List<PackageStore>> GetAllAsync(CancellationToken ct) => Task.FromResult(_rows.Select(Clone).ToList());

    public Task<PackageStore> GetByIdAsync(int id, CancellationToken ct)
    {
        Reads++;

        return Task.FromResult(Clone(_rows.FirstOrDefault(r => r.Id == id)));
    }

    public Task<PackageStore> GetDefaultAsync(CancellationToken ct)
    {
        Reads++;

        return Task.FromResult(Clone(_rows.FirstOrDefault(r => r.IsDefault)));
    }

    public Task<bool> NameExistsAsync(string name, int? exceptId, CancellationToken ct) => Task.FromResult(_rows.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase) && r.Id != exceptId));

    public Task AddAsync(PackageStore store, CancellationToken ct)
    {
        store.Id = _nextId++;
        store.LastModifiedDate = DateTimeOffset.UtcNow;
        _rows.Add(Clone(store));

        return Task.CompletedTask;
    }

    /// <summary>Like the real provider: only name, properties and secret are written; the default flag and the test result stay as the table holds them.</summary>
    public Task UpdateAsync(PackageStore store, bool resetTestResult, CancellationToken ct)
    {
        var row = _rows.FirstOrDefault(r => r.Id == store.Id) ?? throw new Squid.Core.Services.Packages.Storage.Exceptions.PackageStoreNotFoundException(store.Id);

        row.Name = store.Name;
        row.Properties = store.Properties;
        row.Secret = store.Secret;
        row.LastModifiedDate = row.LastModifiedDate.AddSeconds(1);

        if (resetTestResult)
        {
            row.LastTestedDate = null;
            row.LastTestSucceeded = null;
            row.LastTestMessage = null;
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteIfNotDefaultAsync(int id, CancellationToken ct) => Task.FromResult(_rows.RemoveAll(r => r.Id == id && !r.IsDefault) > 0);

    public Task<bool> SetDefaultAsync(int id, CancellationToken ct)
    {
        if (_rows.All(r => r.Id != id)) return Task.FromResult(false);

        foreach (var row in _rows) row.IsDefault = row.Id == id;

        return Task.FromResult(true);
    }

    public Task RecordTestResultAsync(int id, bool success, string message, CancellationToken ct)
    {
        var row = _rows.Single(r => r.Id == id);

        row.LastTestSucceeded = success;
        row.LastTestMessage = message;
        row.LastTestedDate = DateTimeOffset.UtcNow;

        return Task.CompletedTask;
    }

    public PackageStore Remove(int id)
    {
        var row = _rows.Single(r => r.Id == id);

        _rows.Remove(row);

        return Clone(row);
    }

    public void Restore(PackageStore row) => _rows.Add(Clone(row));

    /// <summary>Simulates another replica editing the record: a newer LastModifiedDate.</summary>
    public void Touch(int id) => _rows.Single(r => r.Id == id).LastModifiedDate = _rows.Single(r => r.Id == id).LastModifiedDate.AddSeconds(1);

    private static PackageStore Clone(PackageStore r)
    {
        return r == null ? null : new PackageStore
        {
            Id = r.Id, Name = r.Name, Kind = r.Kind, Properties = r.Properties, Secret = r.Secret, IsDefault = r.IsDefault, LastTestedDate = r.LastTestedDate,
            LastTestSucceeded = r.LastTestSucceeded, LastTestMessage = r.LastTestMessage, CreatedDate = r.CreatedDate, CreatedBy = r.CreatedBy,
            LastModifiedDate = r.LastModifiedDate, LastModifiedBy = r.LastModifiedBy
        };
    }
}

namespace Squid.Core.Persistence.Entities.PackageStorage;

public class PackageStore : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public string Name { get; set; }

    public string Kind { get; set; }

    /// <summary>JSON object of the non-secret settings of <see cref="Kind"/>.</summary>
    public string Properties { get; set; } = "{}";

    /// <summary>JSON object of the secret settings of <see cref="Kind"/>; protected at rest, decrypted only by the data provider on read.</summary>
    public string Secret { get; set; }

    public bool IsDefault { get; set; }

    public DateTimeOffset? LastTestedDate { get; set; }

    public bool? LastTestSucceeded { get; set; }

    public string LastTestMessage { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}

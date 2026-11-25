namespace Squid.Message.Models.PackageStorage;

public class PackageStoreDto : IBaseModel
{
    public int Id { get; set; }

    public string Name { get; set; }

    public string Kind { get; set; }

    /// <summary>The non-secret settings.</summary>
    public Dictionary<string, string> Properties { get; set; } = new();

    /// <summary>Whether a value is stored for each secret setting of the kind. The values themselves are never returned.</summary>
    public Dictionary<string, bool> SecretHasValue { get; set; } = new();

    public bool IsDefault { get; set; }

    public DateTimeOffset? LastTestedDate { get; set; }

    public bool? LastTestSucceeded { get; set; }

    public string LastTestMessage { get; set; }

    /// <summary>Things worth telling the operator about this store, such as a folder that will not survive an upgrade.</summary>
    public List<string> Warnings { get; set; } = new();

    public DateTimeOffset CreatedDate { get; set; }

    public DateTimeOffset LastModifiedDate { get; set; }

    public int LastModifiedBy { get; set; }
}

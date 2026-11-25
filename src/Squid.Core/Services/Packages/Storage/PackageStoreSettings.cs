using Squid.Message.Enums.PackageStorage;
using Squid.Message.Models.PackageStorage;

namespace Squid.Core.Services.Packages.Storage;

/// <summary>Pure rules for the saved settings of a store: what its kind's descriptor allows, and how an edit combines with what is stored.</summary>
public static class PackageStoreSettings
{
    /// <summary>
    /// What is wrong with the settings, empty when they are fine. Non-secret settings must be fields of the kind that are not secret, secret settings
    /// must be secret fields; a required secret may be satisfied by <paramref name="storedSecrets"/> (the edit keeps it).
    /// </summary>
    public static List<string> Validate(PackageStoreProviderDescriptor descriptor, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> secrets, IReadOnlyDictionary<string, string> storedSecrets)
    {
        var problems = new List<string>();

        problems.AddRange(FindUnknownKeys(descriptor, properties, isSecret: false));
        problems.AddRange(FindUnknownKeys(descriptor, secrets, isSecret: true));
        problems.AddRange(FindMissingRequired(descriptor, properties, secrets, storedSecrets));
        problems.AddRange(FindInvalidOptions(descriptor, properties));

        return problems;
    }

    /// <summary>Starts from the stored secrets; a provided non-empty value replaces its stored one, a missing, null or empty one keeps it.</summary>
    public static Dictionary<string, string> MergeSecrets(IReadOnlyDictionary<string, string> stored, IReadOnlyDictionary<string, string> provided)
    {
        var merged = new Dictionary<string, string>(stored);

        foreach (var (key, value) in provided)
            if (!string.IsNullOrEmpty(value)) merged[key] = value;

        return merged;
    }

    private static IEnumerable<string> FindUnknownKeys(PackageStoreProviderDescriptor descriptor, IReadOnlyDictionary<string, string> values, bool isSecret)
    {
        var allowed = descriptor.Fields.Where(f => (f.Kind == PackageStoreFieldKind.Secret) == isSecret).Select(f => f.Key).ToHashSet();

        return values.Keys.Where(key => !allowed.Contains(key)).Select(key => $"\"{key}\" is not a {(isSecret ? "secret " : string.Empty)}setting of the {descriptor.DisplayName} store");
    }

    private static IEnumerable<string> FindMissingRequired(PackageStoreProviderDescriptor descriptor, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> secrets, IReadOnlyDictionary<string, string> storedSecrets)
    {
        return descriptor.Fields.Where(f => f.Required && !HasValue(f, properties, secrets, storedSecrets)).Select(f => $"\"{f.Label}\" is required");
    }

    private static bool HasValue(PackageStoreFieldDescriptor field, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> secrets, IReadOnlyDictionary<string, string> storedSecrets)
    {
        if (field.Kind != PackageStoreFieldKind.Secret) return !string.IsNullOrWhiteSpace(properties.GetValueOrDefault(field.Key));

        return !string.IsNullOrEmpty(secrets.GetValueOrDefault(field.Key)) || !string.IsNullOrEmpty(storedSecrets.GetValueOrDefault(field.Key));
    }

    private static IEnumerable<string> FindInvalidOptions(PackageStoreProviderDescriptor descriptor, IReadOnlyDictionary<string, string> properties)
    {
        foreach (var field in descriptor.Fields.Where(f => f.Kind == PackageStoreFieldKind.Select && f.Options.Count > 0))
        {
            var value = properties.GetValueOrDefault(field.Key);

            if (!string.IsNullOrEmpty(value) && !field.Options.Contains(value))
                yield return $"\"{field.Label}\" must be one of: {string.Join(", ", field.Options)}";
        }
    }
}

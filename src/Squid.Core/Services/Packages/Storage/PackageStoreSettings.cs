using Squid.Message.Enums.PackageStorage;
using Squid.Message.Models.PackageStorage;

namespace Squid.Core.Services.Packages.Storage;

/// <summary>
/// The stored secrets of a store that an edit may keep. <see cref="Reusable"/> are kept as they are; <see cref="Withheld"/> (secret key to the label of the
/// field that changed) are not, because a field the secret is bound to now has a different value.
/// </summary>
public sealed record StoredSecrets(IReadOnlyDictionary<string, string> Reusable, IReadOnlyDictionary<string, string> Withheld)
{
    public static StoredSecrets None { get; } = new(new Dictionary<string, string>(), new Dictionary<string, string>());
}

/// <summary>Pure rules for the saved settings of a store: what its kind's descriptor allows, and how an edit combines with what is stored.</summary>
public static class PackageStoreSettings
{
    /// <summary>
    /// What is wrong with the settings, empty when they are fine. Non-secret settings must be fields of the kind that are not secret, secret settings
    /// must be secret fields; a required secret may be satisfied by the reusable stored secrets (the edit keeps it).
    /// </summary>
    public static List<string> Validate(PackageStoreProviderDescriptor descriptor, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> secrets, StoredSecrets stored)
    {
        var problems = new List<string>();

        problems.AddRange(FindUnknownKeys(descriptor, properties, isSecret: false));
        problems.AddRange(FindUnknownKeys(descriptor, secrets, isSecret: true));
        problems.AddRange(FindMissingRequired(descriptor, properties, secrets, stored));
        problems.AddRange(FindInvalidOptions(descriptor, properties));

        return problems;
    }

    /// <summary>
    /// Splits the stored secrets into those the edit may keep and those it may not: a secret bound to fields (see
    /// <see cref="PackageStoreFieldDescriptor.BoundTo"/>) is only kept while every bound field still has its stored value.
    /// </summary>
    public static StoredSecrets SelectReusable(PackageStoreProviderDescriptor descriptor, IReadOnlyDictionary<string, string> newProperties, IReadOnlyDictionary<string, string> storedProperties, IReadOnlyDictionary<string, string> storedSecrets)
    {
        var reusable = new Dictionary<string, string>();
        var withheld = new Dictionary<string, string>();

        foreach (var (key, value) in storedSecrets)
        {
            var changed = FindChangedBoundField(descriptor, key, newProperties, storedProperties);

            if (changed == null) reusable[key] = value;
            else withheld[key] = changed;
        }

        return new StoredSecrets(reusable, withheld);
    }

    /// <summary>Starts from the reusable stored secrets; a provided non-empty value replaces its stored one, a missing, null or empty one keeps it.</summary>
    public static Dictionary<string, string> MergeSecrets(StoredSecrets stored, IReadOnlyDictionary<string, string> provided)
    {
        var merged = new Dictionary<string, string>(stored.Reusable);

        foreach (var (key, value) in provided)
            if (!string.IsNullOrEmpty(value)) merged[key] = value;

        return merged;
    }

    /// <summary>The label of the first bound field whose value differs from the stored one, or null when the secret may be kept.</summary>
    private static string FindChangedBoundField(PackageStoreProviderDescriptor descriptor, string secretKey, IReadOnlyDictionary<string, string> newProperties, IReadOnlyDictionary<string, string> storedProperties)
    {
        var field = descriptor.Fields.FirstOrDefault(f => f.Kind == PackageStoreFieldKind.Secret && f.Key == secretKey);

        foreach (var boundKey in field?.BoundTo ?? [])
            if (!string.Equals(newProperties.GetValueOrDefault(boundKey) ?? string.Empty, storedProperties.GetValueOrDefault(boundKey) ?? string.Empty, StringComparison.Ordinal))
                return descriptor.Fields.FirstOrDefault(f => f.Key == boundKey)?.Label ?? boundKey;

        return null;
    }

    private static IEnumerable<string> FindUnknownKeys(PackageStoreProviderDescriptor descriptor, IReadOnlyDictionary<string, string> values, bool isSecret)
    {
        var allowed = descriptor.Fields.Where(f => (f.Kind == PackageStoreFieldKind.Secret) == isSecret).Select(f => f.Key).ToHashSet();

        return values.Keys.Where(key => !allowed.Contains(key)).Select(key => $"\"{key}\" is not a {(isSecret ? "secret " : string.Empty)}setting of the {descriptor.DisplayName} store");
    }

    private static IEnumerable<string> FindMissingRequired(PackageStoreProviderDescriptor descriptor, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> secrets, StoredSecrets stored)
    {
        return descriptor.Fields.Where(f => f.Required && !HasValue(f, properties, secrets, stored)).Select(f => MissingMessage(f, stored));
    }

    private static string MissingMessage(PackageStoreFieldDescriptor field, StoredSecrets stored)
    {
        return stored.Withheld.TryGetValue(field.Key, out var changed)
            ? $"\"{field.Label}\" must be entered again because \"{changed}\" was changed"
            : $"\"{field.Label}\" is required";
    }

    private static bool HasValue(PackageStoreFieldDescriptor field, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> secrets, StoredSecrets stored)
    {
        if (field.Kind != PackageStoreFieldKind.Secret) return !string.IsNullOrWhiteSpace(properties.GetValueOrDefault(field.Key));

        return !string.IsNullOrEmpty(secrets.GetValueOrDefault(field.Key)) || !string.IsNullOrEmpty(stored.Reusable.GetValueOrDefault(field.Key));
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

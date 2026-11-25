using Npgsql;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Providers.FileSystem;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;
using Squid.Message.Models.Setup;

namespace Squid.Core.Setup.Install;

public sealed partial class SetupInstaller
{
    private const string UpdateDefaultPackageStoreSql = "UPDATE package_store SET name = @name, kind = @kind, properties = @properties, secret = @secret, last_modified_date = now() WHERE is_default";

    /// <summary>Points the default store the schema was seeded with at the chosen kind; a missing or default choice leaves the seeded row as it is.</summary>
    private async Task<string> ApplyPackageStorageAsync(CancellationToken ct)
    {
        var choice = _ctx.Request.PackageStorage;

        if (SetupPackageStorage.IsSeededDefault(choice)) return "Using the default local disk store";

        var provider = PackageStoreSettingsCheck.ResolveProvider(_packageStoreProviders, choice.Kind);

        PackageStoreSettingsCheck.EnsureValid(provider, choice.Properties, choice.Secrets, new Dictionary<string, string>());

        await UpdateDefaultPackageStoreAsync(provider.Describe().DisplayName, choice, ct).ConfigureAwait(false);

        return $"Packages are kept in the \"{provider.Describe().DisplayName}\" store";
    }

    private async Task UpdateDefaultPackageStoreAsync(string name, SetupPackageStorageChoice choice, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_ctx.DatabaseConnectionString);

        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(UpdateDefaultPackageStoreSql, connection);

        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("kind", choice.Kind);
        command.Parameters.AddWithValue("properties", PackageStoreConfigurationMapper.SerializeSettings(choice.Properties ?? new Dictionary<string, string>()) ?? "{}");
        command.Parameters.AddWithValue("secret", (object)ProtectSecrets(choice) ?? DBNull.Value);

        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        if (rows != 1)
            throw new InvalidOperationException("The default package store was not found in the new database");
    }

    /// <summary>Encrypted with the same protector and master key (already in the configuration) the server uses once set up.</summary>
    private string ProtectSecrets(SetupPackageStorageChoice choice)
    {
        var present = (choice.Secrets ?? new Dictionary<string, string>()).Where(pair => !string.IsNullOrEmpty(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var json = PackageStoreConfigurationMapper.SerializeSettings(present);

        if (json == null) return null;

        var protector = new AtRestSecretProtector(new VariableEncryptionService(new SecuritySetting(_configuration)));

        return protector.Protect(json, PackageStoreDataProvider.SecretKdfScope);
    }
}

/// <summary>What the wizard's package storage choice means for the seeded default store.</summary>
public static class SetupPackageStorage
{
    /// <summary>True for no choice, or the local disk store with every setting empty: exactly what the schema seeds, so nothing needs writing.</summary>
    public static bool IsSeededDefault(SetupPackageStorageChoice choice)
    {
        if (choice == null) return true;

        var propertiesEmpty = (choice.Properties ?? new Dictionary<string, string>()).Values.All(string.IsNullOrWhiteSpace);
        var secretsEmpty = (choice.Secrets ?? new Dictionary<string, string>()).Values.All(string.IsNullOrEmpty);

        return choice.Kind == FileSystemPackageBlobStore.StoreKind && propertiesEmpty && secretsEmpty;
    }
}

using Npgsql;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Providers.FileSystem;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;
using Squid.Message.Models.Setup;

namespace Squid.Core.Setup.Install;

public sealed partial class SetupInstaller
{
    public const string ExistingPackageStorageKeptMessage = "Existing package storage kept; change it in Settings";

    private const string UniqueViolation = "23505";
    private const string UpdateDefaultPackageStoreSql = "UPDATE package_store SET name = @name, kind = @kind, properties = @properties, secret = @secret, last_modified_date = now() WHERE is_default";

    /// <summary>
    /// On a fresh install (the schema did not exist when the setup first looked) the default store is the migration's seeded row, so it is set to the
    /// chosen kind, or back to the seeded values for a default choice (an earlier attempt of this session may have changed it). Against a database that
    /// already held a Squid schema, the stores are the operator's and are left alone.
    /// </summary>
    private async Task<string> ApplyPackageStorageAsync(CancellationToken ct)
    {
        if (!_schemaOrigin.WasFresh(_ctx.DatabaseConnectionString)) return ExistingPackageStorageKeptMessage;

        var choice = _ctx.Request.PackageStorage;

        if (SetupPackageStorage.IsSeededDefault(choice))
        {
            await WriteDefaultPackageStoreAsync(new DefaultStoreValues(SetupPackageStorage.SeededName, FileSystemPackageBlobStore.StoreKind, "{}", null), ct).ConfigureAwait(false);

            return "Using the default local disk store";
        }

        var provider = PackageStoreSettingsCheck.ResolveProvider(_packageStoreProviders, choice.Kind);

        PackageStoreSettingsCheck.PrepareConfiguration(provider, choice.Properties, choice.Secrets, StoredSecrets.None);

        await WriteDefaultPackageStoreAsync(BuildValues(provider.Describe().DisplayName, choice), ct).ConfigureAwait(false);

        return $"Packages are kept in the \"{provider.Describe().DisplayName}\" store";
    }

    private DefaultStoreValues BuildValues(string name, SetupPackageStorageChoice choice)
    {
        var properties = PackageStoreConfigurationMapper.SerializeSettings(choice.Properties ?? new Dictionary<string, string>()) ?? "{}";

        return new DefaultStoreValues(name, choice.Kind, properties, ProtectSecrets(choice));
    }

    private async Task WriteDefaultPackageStoreAsync(DefaultStoreValues values, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_ctx.DatabaseConnectionString);

        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(UpdateDefaultPackageStoreSql, connection);

        command.Parameters.AddWithValue("name", values.Name);
        command.Parameters.AddWithValue("kind", values.Kind);
        command.Parameters.AddWithValue("properties", values.Properties);
        command.Parameters.AddWithValue("secret", (object)values.Secret ?? DBNull.Value);

        var rows = await ExecuteUpdateAsync(command, values, ct).ConfigureAwait(false);

        if (rows != 1)
            throw new InvalidOperationException("The new database has no default package store to set up (the schema migration seeds one). Nothing was changed; check the package_store table or set the package storage in Settings after installing.");
    }

    private static async Task<int> ExecuteUpdateAsync(NpgsqlCommand command, DefaultStoreValues values, CancellationToken ct)
    {
        try
        {
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
        {
            throw new InvalidOperationException($"Another package store is already named \"{values.Name}\" in the database, so the default store cannot be set up under that name. Remove or rename that store, or set the package storage in Settings after installing.");
        }
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

    private sealed record DefaultStoreValues(string Name, string Kind, string Properties, string Secret);
}

/// <summary>What the wizard's package storage choice means for the seeded default store.</summary>
public static class SetupPackageStorage
{
    /// <summary>The name the schema migration gives the store it seeds.</summary>
    public const string SeededName = "Local disk";

    /// <summary>True for no choice, or the local disk store with every setting empty: exactly what the schema seeds.</summary>
    public static bool IsSeededDefault(SetupPackageStorageChoice choice)
    {
        if (choice == null) return true;

        var propertiesEmpty = (choice.Properties ?? new Dictionary<string, string>()).Values.All(string.IsNullOrWhiteSpace);
        var secretsEmpty = (choice.Secrets ?? new Dictionary<string, string>()).Values.All(string.IsNullOrEmpty);

        return choice.Kind == FileSystemPackageBlobStore.StoreKind && propertiesEmpty && secretsEmpty;
    }
}

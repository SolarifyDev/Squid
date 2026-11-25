using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Message.Models.Setup;

namespace Squid.Core.Setup.Install;

/// <summary>Checks a setup request before any side effect, so the wizard can show every problem at once.</summary>
public static class SetupRequestValidator
{
    public const int MinimumPasswordLength = 8;

    /// <param name="providers">The package store providers a package storage choice is checked against; defaults to the built-in ones (validation looks at descriptors only, so no data directory is needed).</param>
    public static void EnsureValid(SetupInstallRequest request, SetupCapabilities capabilities, IReadOnlyList<IPackageBlobStoreProvider> providers = null)
    {
        var errors = new Dictionary<string, string>();

        ValidateDatabase(request.Database, capabilities, errors);
        ValidateRedis(request.Redis, capabilities, errors);
        ValidateAdmin(request.Admin, errors);
        ValidateServerUrls(request.Server, errors);
        ValidatePackageStorage(request.PackageStorage, providers ?? PackageStoreProviderCatalog.Create(new ConfigurationBuilder().Build()), errors);

        if (errors.Count > 0) throw new SetupValidationException(errors);
    }

    private static void ValidateDatabase(SetupDatabaseChoice database, SetupCapabilities capabilities, Dictionary<string, string> errors)
    {
        if (database.Mode == SetupServiceMode.Builtin && !capabilities.BuiltinDatabaseAvailable) errors["database.mode"] = "Built-in PostgreSQL is not available in this installation";
        if (database.Mode == SetupServiceMode.External && !IsParsableDatabaseConnection(database.ConnectionString, out var error)) errors["database.connectionString"] = error;
    }

    private static void ValidateRedis(SetupRedisChoice redis, SetupCapabilities capabilities, Dictionary<string, string> errors)
    {
        if (redis.Mode == SetupServiceMode.Builtin && !capabilities.BuiltinRedisAvailable) errors["redis.mode"] = "Built-in Redis is not available in this installation";
        if (redis.Mode == SetupServiceMode.External && string.IsNullOrWhiteSpace(redis.ConnectionString)) errors["redis.connectionString"] = "Redis connection string is required";
    }

    private static void ValidateAdmin(SetupAdminAccount admin, Dictionary<string, string> errors)
    {
        if (string.IsNullOrWhiteSpace(admin.UserName)) errors["admin.userName"] = "Administrator user name is required";
        if ((admin.Password ?? string.Empty).Length < MinimumPasswordLength) errors["admin.password"] = $"Password must be at least {MinimumPasswordLength} characters";
    }

    private static void ValidateServerUrls(SetupServerUrls server, Dictionary<string, string> errors)
    {
        if (!IsHttpUrl(server.ExternalUrl)) errors["server.externalUrl"] = "External URL must be an absolute http(s) URL";
        if (!string.IsNullOrWhiteSpace(server.CommsUrl) && !IsHttpUrl(server.CommsUrl)) errors["server.commsUrl"] = "Polling URL must be an absolute http(s) URL";
    }

    private static void ValidatePackageStorage(SetupPackageStorageChoice choice, IReadOnlyList<IPackageBlobStoreProvider> providers, Dictionary<string, string> errors)
    {
        if (choice == null) return;

        try
        {
            var provider = PackageStoreSettingsCheck.ResolveProvider(providers, choice.Kind);

            PackageStoreSettingsCheck.EnsureValid(provider, choice.Properties, choice.Secrets, new Dictionary<string, string>());
        }
        catch (PackageStoreProviderNotFoundException ex)
        {
            errors["packageStorage.kind"] = ex.Message;
        }
        catch (PackageStoreConfigurationInvalidException ex)
        {
            errors["packageStorage"] = string.Join("; ", ex.Problems);
        }
    }

    private static bool IsParsableDatabaseConnection(string connectionString, out string error)
    {
        try
        {
            DatabaseConnectionTester.ParseOrThrow(connectionString);
            error = null;

            return true;
        }
        catch (ArgumentException ex)
        {
            error = $"Invalid PostgreSQL connection string: {ex.Message}";

            return false;
        }
    }

    private static bool IsHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>What this installation can offer, decided by which executables the image ships.</summary>
public sealed record SetupCapabilities(bool BuiltinDatabaseAvailable, bool BuiltinRedisAvailable);

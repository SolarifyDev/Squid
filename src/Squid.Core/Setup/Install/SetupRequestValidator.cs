using Squid.Message.Models.Setup;

namespace Squid.Core.Setup.Install;

/// <summary>Checks a setup request before any side effect, so the wizard can show every problem at once.</summary>
public static class SetupRequestValidator
{
    public const int MinimumPasswordLength = 8;

    public static void EnsureValid(SetupInstallRequest request, SetupCapabilities capabilities)
    {
        var errors = new Dictionary<string, string>();

        ValidateDatabase(request.Database, capabilities, errors);
        ValidateRedis(request.Redis, capabilities, errors);
        ValidateAdmin(request.Admin, errors);
        ValidateServerUrls(request.Server, errors);

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

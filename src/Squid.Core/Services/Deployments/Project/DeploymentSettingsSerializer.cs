using System.Text.Json;
using Squid.Message.Models.Deployments.Project;

namespace Squid.Core.Services.Deployments.Project;

/// <summary>
/// Shared (de)serialisation for the project's <see cref="DeploymentSettingsDto"/> JSON
/// blob. A null / blank / malformed blob deserialises to a default-constructed DTO so
/// every consumer (the deploy pipeline + the get/save service) sees the all-defaults DTO
/// rather than throwing.
/// </summary>
public static class DeploymentSettingsSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static DeploymentSettingsDto Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new DeploymentSettingsDto();

        try
        {
            return JsonSerializer.Deserialize<DeploymentSettingsDto>(json, Options) ?? new DeploymentSettingsDto();
        }
        catch (JsonException)
        {
            return new DeploymentSettingsDto();
        }
    }

    public static string Serialize(DeploymentSettingsDto settings)
        => JsonSerializer.Serialize(settings ?? new DeploymentSettingsDto(), Options);
}

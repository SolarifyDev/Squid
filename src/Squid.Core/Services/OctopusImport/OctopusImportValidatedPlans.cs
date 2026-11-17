using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>Reads what a session's stored validated plan says the user chose.</summary>
public static class OctopusImportValidatedPlans
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The choices the plan was validated with; none for a session without a validated plan.</summary>
    public static OctopusImportChoiceSelections SavedChoices(string validatedPlanJson)
    {
        if (string.IsNullOrWhiteSpace(validatedPlanJson)) return OctopusImportChoiceSelections.Empty;

        return OctopusImportChoiceSelections.From(JsonSerializer.Deserialize<OctopusImportValidatedPlanDto>(validatedPlanJson, JsonOptions)?.PreviewPlan);
    }
}

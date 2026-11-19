using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The secrets the Octopus server withheld on a feed, account, step or action — the one list preview and confirmation both
/// report, so what the preview says will be missing is exactly what the import result lists as missing. Variable values are
/// reported by the variable rules (<see cref="OctopusImportRequiredInputBuilder.ForSensitiveVariable"/>). Empty outside a
/// server session: a marker in an export file is never reported as "withheld by the server".
/// </summary>
public static class OctopusImportWithheldSecretInputs
{
    public const string FeedPasswordField = "Password";

    public static IReadOnlyList<OctopusImportRequiredInputDto> For(OctopusResourceNode resource, OctopusExportSecrets secrets)
    {
        if (resource == null || secrets?.IsWithheldByServer != true) return [];

        return Fields(resource)
            .Select(field => OctopusImportRequiredInputBuilder.ForWithheldSecret(field.Kind, resource, field.Name))
            .ToList();
    }

    private static IEnumerable<(OctopusImportRequiredInputKind Kind, string Name)> Fields(OctopusResourceNode resource)
        => resource.Kind switch
        {
            OctopusResourceKind.Feed => Named(OctopusImportRequiredInputKind.FeedPassword, OctopusImportWithheldSecrets.For(resource.GetSource<OctopusFeedDto>()?.ExtensionData).Where(IsFeedPassword)),
            OctopusResourceKind.Account => Named(OctopusImportRequiredInputKind.AccountSecret, OctopusImportWithheldSecrets.For(resource.GetSource<OctopusAccountDto>()?.Credentials)),
            OctopusResourceKind.DeploymentStep => Named(OctopusImportRequiredInputKind.SensitiveActionProperty, OctopusImportWithheldSecrets.For(resource.GetSource<OctopusDeploymentStepDto>()?.ExtensionData)),
            OctopusResourceKind.DeploymentAction => Named(OctopusImportRequiredInputKind.SensitiveActionProperty, OctopusImportWithheldSecrets.For(resource.GetSource<OctopusDeploymentActionDto>()?.ExtensionData)),
            _ => []
        };

    private static bool IsFeedPassword(string member) => string.Equals(member, FeedPasswordField, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(OctopusImportRequiredInputKind, string)> Named(OctopusImportRequiredInputKind kind, IEnumerable<string> members)
        => members.Order(StringComparer.OrdinalIgnoreCase).Select(member => (kind, member));
}

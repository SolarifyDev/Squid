using Squid.Core.Extensions;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Handlers;

public class DeployPackageActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.TentaclePackage;

    public IReadOnlyDictionary<string, IReadOnlySet<string>> StaticRequirements { get; } =
        CapabilityRequirements.Empty
            .Require(CapabilityKeys.OsSlot,
                CapabilityKeys.Os.Windows,
                CapabilityKeys.Os.Linux,
                CapabilityKeys.Os.MacOS);

    Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var action = ctx.Action ?? throw new DeploymentValidationException("DeployPackage action is missing.");
        var feedId = action.GetProperty(SpecialVariables.Action.PackageFeedId);
        var packageId = action.GetProperty(SpecialVariables.Action.PackageId);

        if (string.IsNullOrWhiteSpace(feedId) || string.IsNullOrWhiteSpace(packageId))
            throw new DeploymentValidationException(
                $"DeployPackage action '{action.Name}' requires FeedId and PackageId.");

        var mode = action.GetProperty(SpecialVariables.Action.InstallationDirectoryMode);
        if (string.IsNullOrWhiteSpace(mode))
            mode = "Versioned";

        if (!string.Equals(mode, "Versioned", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            throw new DeploymentValidationException($"Unsupported installation directory mode '{mode}'.");
        }

        var selected = ctx.SelectedPackages?
            .Where(sp => string.Equals(sp.ActionName, action.Name, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(sp.PackageReferenceName, packageId, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        if (selected.Count != 1 || string.IsNullOrWhiteSpace(selected[0].Version))
        {
            throw new DeploymentValidationException(
                $"DeployPackage action '{action.Name}' has no unique Release-selected version for package '{packageId}'.");
        }

        var version = selected[0].Version;
        var env = ctx.Variables?.FirstOrDefault(v =>
                      string.Equals(v.Name, "Squid.Environment.Name", StringComparison.OrdinalIgnoreCase))?.Value
                  ?? throw new DeploymentValidationException("Squid.Environment.Name is required.");
        var project = ctx.Variables?.FirstOrDefault(v =>
                          string.Equals(v.Name, "Squid.Project.Name", StringComparison.OrdinalIgnoreCase))?.Value
                      ?? throw new DeploymentValidationException("Squid.Project.Name is required.");

        var segments = new PackageInstallationPathSegments(
            PackageInstallationPath.SanitizeSegment(env, "Environment"),
            PackageInstallationPath.SanitizeSegment(project, "Project"),
            PackageInstallationPath.SanitizeSegment(packageId, "Package"),
            PackageInstallationPath.SanitizeSegment(version, "Version"));

        var customDir = action.GetProperty(SpecialVariables.Action.CustomInstallationDirectory) ?? string.Empty;
        var normalizedMode = string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase) ? "Custom" : "Versioned";
        if (normalizedMode == "Custom" && string.IsNullOrWhiteSpace(customDir))
            throw new DeploymentValidationException("Custom installation directory is required when mode is Custom.");

        var package = new IntentPackageReference
        {
            PackageId = packageId,
            Version = version,
            FeedId = feedId
        };

        return Task.FromResult<ExecutionIntent>(new DeployPackageIntent
        {
            Name = "deploy-package",
            StepName = ctx.Step?.Name ?? string.Empty,
            ActionName = action.Name,
            Package = package,
            InstallationDirectoryMode = normalizedMode,
            CustomInstallationDirectory = customDir,
            PathSegments = segments,
            ScriptSyntax = ScriptSyntax.Bash,
            Packages = new[] { package },
            RequiredCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                IntentCapabilityKeys.PackageStaging
            }
        });
    }
}

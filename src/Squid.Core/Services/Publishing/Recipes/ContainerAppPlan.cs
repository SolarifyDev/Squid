using System.Globalization;
using Squid.Core.Services.Publishing.Exceptions;
using Squid.Core.Services.Publishing.Inspectors;
using Squid.Core.Utils;
using Squid.Message.Constants;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing.Recipes;

/// <summary>What the container recipes share: the two-step plan (extract the package, run a script), the names derived from the package, and the values the shared script template needs.</summary>
public static class ContainerAppPlan
{
    public const string DeployPackageStepName = "Deploy package";
    public const int HealthTimeoutSeconds = 60;
    public const string DefaultHealthPath = "/";
    public const string ContainerNamePrefix = "squid-app-";

    private const int MaxSlugLength = 48;
    private const string FallbackSlug = "app";
    private const string InstallationDirectoryMode = "Versioned";

    /// <summary>
    /// The folder the package was extracted into, as the output variable the deploy-package step publishes. The deployment replaces it in the script before the
    /// script runs, on the same target (the folder is per environment, project, package and version, so every target resolves to the same path).
    /// </summary>
    public static string InstallDirToken { get; } = "#{" + SpecialVariables.Output.Variable(DeployPackageStepName, SpecialVariables.Action.InstallationDirectoryPath) + "}";

    public static string ContainerName(string packageId)
    {
        var slug = SlugGenerator.Generate(packageId);

        slug = slug.Length > MaxSlugLength ? slug[..MaxSlugLength].Trim('-') : slug;

        return ContainerNamePrefix + (slug.Length == 0 ? FallbackSlug : slug);
    }

    /// <summary>The values of the placeholders every container script has. A recipe adds its own and renders its template with them.</summary>
    public static Dictionary<string, string> CommonValues(RecipeRenderContext context, string healthPath, int containerPort)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppName"] = ShellQuote.Literal("packageId", context.PackageId),
            ["ContainerName"] = ShellQuote.Literal("packageId", ContainerName(context.PackageId)),
            ["HostPort"] = context.HostPort.ToString(CultureInfo.InvariantCulture),
            ["ContainerPort"] = containerPort.ToString(CultureInfo.InvariantCulture),
            ["HealthPath"] = ShellQuote.Literal(PackageFactKeys.SquidHealthPath, healthPath),
            ["HealthTimeoutSeconds"] = HealthTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["InstallDir"] = "'" + InstallDirToken + "'"
        };
    }

    /// <summary>The path <c>squid.yaml</c> asked to be checked, with a leading slash; <c>/</c> when it asked for none.</summary>
    public static string HealthPath(PackageFacts facts)
    {
        var path = facts.Get(PackageFactKeys.SquidHealthPath)?.Trim();

        if (string.IsNullOrEmpty(path)) return DefaultHealthPath;

        if (path.Any(char.IsWhiteSpace))
            throw new RecipeInputInvalidException(PackageFactKeys.SquidHealthPath, $"'{path}' has whitespace in it");

        return path.StartsWith('/') ? path : "/" + path;
    }

    /// <summary>The folder under the extraction folder that holds the app: the archive's single top folder, then the given folder inside it. Empty when the app is at the top.</summary>
    public static string SubPath(PackageFacts facts, string folderInsideRoot)
    {
        var parts = new[] { facts.Get(PackageFactKeys.ArchiveRoot), folderInsideRoot }.Where(p => !string.IsNullOrEmpty(p) && p != FileMarkerInspector.RootFolder);

        return string.Join('/', parts);
    }

    public static int ParsePort(string input, string value)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535) return port;

        throw new RecipeInputInvalidException(input, $"'{value}' is not a TCP port between 1 and 65535");
    }

    public static DeploymentPlan Build(RecipeRenderContext context, string runStepName, string script)
    {
        return new DeploymentPlan
        {
            Inputs = [new PlanInput { Name = PlanInputNames.HostPort, Description = "The port on the target the app is published on." }],
            Steps =
            [
                Step(context, DeployPackageStepName, DeployPackageAction(context)),
                Step(context, runStepName, RunScriptAction(runStepName, script))
            ]
        };
    }

    private static PlanStep Step(RecipeRenderContext context, string name, PlanAction action)
    {
        return new PlanStep { Name = name, TargetRoles = context.TargetRoles.ToList(), Actions = [action] };
    }

    private static PlanAction DeployPackageAction(RecipeRenderContext context)
    {
        return new PlanAction
        {
            Name = DeployPackageStepName,
            ActionType = SpecialVariables.ActionTypes.TentaclePackage,
            Package = new PlanPackageReference { PackageId = context.PackageId },
            Properties = new Dictionary<string, string> { [SpecialVariables.Action.InstallationDirectoryMode] = InstallationDirectoryMode }
        };
    }

    private static PlanAction RunScriptAction(string name, string script)
    {
        var properties = new Dictionary<string, string>
        {
            [SpecialVariables.Action.ScriptBody] = script,
            [SpecialVariables.Action.ScriptSyntax] = SpecialVariables.ScriptSyntax.Bash
        };

        return new PlanAction { Name = name, ActionType = SpecialVariables.ActionTypes.Script, Properties = properties };
    }
}

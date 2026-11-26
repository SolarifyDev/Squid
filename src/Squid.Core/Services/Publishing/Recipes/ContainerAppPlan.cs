using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    public const string ContainerNamePrefix = "squid-";

    private const int MaxSlugLength = 28;
    private const int HashLength = 6;
    private const string FallbackSlug = "app";
    private const string InstallationDirectoryMode = "Versioned";

    /// <summary>The name of the output variable the deploy-package step publishes: the folder the package was extracted into.</summary>
    public static string InstallDirVariable { get; } = SpecialVariables.Output.Variable(DeployPackageStepName, SpecialVariables.Action.InstallationDirectoryPath);

    /// <summary>
    /// A deployment variable as a script placeholder value. The deployment substitutes <c>#{...}</c> as plain text, and a name (an environment called <c>x'$(cmd)'</c>)
    /// can hold anything, so the value is Base64 encoded by the deployment (a text of letters, digits, <c>+/=</c>) and decoded by the script's prelude.
    /// </summary>
    public static string VariableB64(string variable) => "'#{" + variable + " | ToBase64}'";

    /// <summary>
    /// The readable part of the container and image name: <c>squid-</c> and the slugged package id. A slug longer than the cap is cut and a short hash of the whole id is
    /// appended, so two long ids that share a prefix still differ. The deploy-time part (<c>-p{project id}-e{environment id}</c>) is added by the script.
    /// </summary>
    public static string ContainerNameBase(string packageId)
    {
        var slug = SlugGenerator.Generate(packageId);

        if (slug.Length == 0) slug = FallbackSlug;

        return ContainerNamePrefix + (slug.Length > MaxSlugLength ? slug[..(MaxSlugLength - HashLength - 1)].Trim('-') + "-" + Hash(packageId) : slug);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant()))).ToLowerInvariant()[..HashLength];

    /// <summary>The values of the placeholders every container script has. A recipe adds its own and renders its template with them.</summary>
    public static Dictionary<string, string> CommonValues(RecipeRenderContext context, string healthPath, int containerPort)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppName"] = ShellQuote.Literal("packageId", context.PackageId),
            ["ContainerNameBase"] = ShellQuote.Literal("packageId", ContainerNameBase(context.PackageId)),
            ["ProjectIdB64"] = VariableB64(SpecialVariables.Project.Id),
            ["EnvironmentIdB64"] = VariableB64(SpecialVariables.Environment.Id),
            ["HostPortB64"] = VariableB64(PlanInputVariables.Name(PlanInputNames.HostPort)),
            ["ContainerPort"] = containerPort.ToString(CultureInfo.InvariantCulture),
            ["HealthPath"] = ShellQuote.Literal(PackageFactKeys.SquidHealthPath, healthPath),
            ["HealthTimeoutSeconds"] = HealthTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["InstallDirB64"] = VariableB64(InstallDirVariable)
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
            Inputs = [new PlanInput { Name = PlanInputNames.HostPort, Description = "The port on the target the app is published on in this environment.", Type = PlanInputType.TcpPort }],
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

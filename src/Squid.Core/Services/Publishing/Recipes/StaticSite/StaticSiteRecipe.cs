using Squid.Core.Services.Publishing.Inspectors;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing.Recipes.StaticSite;

/// <summary>Serves a package that holds an <c>index.html</c> with a Caddy container: the package is extracted on the target and mounted read-only as the site.</summary>
public sealed class StaticSiteRecipe : IDeployRecipe
{
    public const string RecipeKind = "static-site";
    /// <summary>An exact version, so a deployment never runs a different server than the one this recipe was tested with.</summary>
    public const string ServerImage = "caddy:2.11.9";
    public const int ServerPort = 80;
    public const int FitsScore = 70;
    public const int DockerfilePresentScore = 20;

    private const string RunStepName = "Serve site";
    private const string Template = "StaticSite";

    public string Kind => RecipeKind;

    public RecipeMatch Match(PackageFacts facts)
    {
        if (!facts.Is(PackageFactKeys.MarkerIndexHtml)) return new RecipeMatch { Reasons = ["The package has no index.html"] };

        var reasons = new List<string> { $"The package has an index.html in '{IndexFolder(facts)}'" };

        if (!facts.Is(PackageFactKeys.MarkerDockerfile)) return new RecipeMatch { Score = FitsScore, Reasons = reasons };

        reasons.Add("The package also has a Dockerfile, which says it is meant to run as an app");

        return new RecipeMatch { Score = DockerfilePresentScore, Reasons = reasons };
    }

    public Task<DeploymentPlan> RenderAsync(RecipeRenderContext context, CancellationToken ct)
    {
        var values = ContainerAppPlan.CommonValues(context, ContainerAppPlan.HealthPath(context.Facts), ServerPort);

        values["SiteSubPath"] = ShellQuote.Literal("archive.root", ContainerAppPlan.SubPath(context.Facts, IndexFolder(context.Facts)));
        values["ServerImage"] = ShellQuote.Literal("serverImage", ServerImage);

        return Task.FromResult(ContainerAppPlan.Build(context, RunStepName, RecipeScript.Render(Template, values)));
    }

    private static string IndexFolder(PackageFacts facts) => facts.Get(PackageFactKeys.MarkerIndexHtmlDir) ?? FileMarkerInspector.RootFolder;
}

using Squid.Core.Services.Publishing.Exceptions;
using Squid.Message.Constants;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing.Recipes.Dockerfile;

/// <summary>Builds the Dockerfile the package carries on the target and runs the image, the port the app listens on coming from <c>squid.yaml</c> or the Dockerfile's first <c>EXPOSE</c>.</summary>
public sealed class DockerfileRecipe : IDeployRecipe
{
    public const string RecipeKind = "dockerfile";
    public const int FitsScore = 80;

    private const string RunStepName = "Build and run";
    private const string Template = "Dockerfile";
    
    public string Kind => RecipeKind;

    public RecipeMatch Match(PackageFacts facts)
    {
        if (!facts.Is(PackageFactKeys.MarkerDockerfile)) return new RecipeMatch { Reasons = ["The package has no Dockerfile"] };

        return new RecipeMatch { Score = FitsScore, Reasons = ["The package has a Dockerfile"] };
    }

    public Task<DeploymentPlan> RenderAsync(RecipeRenderContext context, CancellationToken ct)
    {
        var values = ContainerAppPlan.CommonValues(context, ContainerAppPlan.HealthPath(context.Facts), ResolveContainerPort(context.Facts));

        values["BuildSubPath"] = ShellQuote.Literal("archive.root", ContainerAppPlan.SubPath(context.Facts, null));
        values["ReleaseVersionB64"] = ContainerAppPlan.VariableB64(SpecialVariables.Release.Number);

        return Task.FromResult(ContainerAppPlan.Build(context, RunStepName, RecipeScript.Render(Template, values)));
    }

    private static int ResolveContainerPort(PackageFacts facts)
    {
        var declared = facts.Get(PackageFactKeys.SquidRunPort);

        if (!string.IsNullOrWhiteSpace(declared)) return ContainerAppPlan.ParsePort(PackageFactKeys.SquidRunPort, declared.Trim());

        var exposed = facts.Get(PackageFactKeys.DockerfileExposePort);

        if (!string.IsNullOrWhiteSpace(exposed)) return ContainerAppPlan.ParsePort(PackageFactKeys.DockerfileExposePort, exposed.Trim());

        throw new RecipeInputInvalidException("containerPort", "the Dockerfile has no EXPOSE and squid.yaml has no run.port, so the port the app listens on is unknown. Add 'run: { port: ... }' to squid.yaml or an EXPOSE to the Dockerfile");
    }
}

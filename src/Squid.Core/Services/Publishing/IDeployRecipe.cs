using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

/// <summary>A way to run a kind of package: it says how well it fits a package's facts, and renders the deployment plan that runs it. A recipe is stateless and never touches the database.</summary>
public interface IDeployRecipe : IScopedDependency
{
    /// <summary>The recipe's stable name: what <c>recipe:</c> in <c>squid.yaml</c> and a user's choice refer to. Matched ignoring case.</summary>
    string Kind { get; }

    /// <summary>A score of 0 means the recipe does not apply; a higher score fits better. The reasons say why, for the person choosing.</summary>
    RecipeMatch Match(PackageFacts facts);

    /// <summary>The steps that deploy a package this recipe matched. Throws <c>RecipeInputInvalidException</c> when the facts or the request leave out something the plan cannot do without.</summary>
    Task<DeploymentPlan> RenderAsync(RecipeRenderContext context, CancellationToken ct);
}

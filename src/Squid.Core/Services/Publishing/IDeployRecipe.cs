using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

/// <summary>A way to run a kind of package: it says how well it fits a package's facts. Rendering a deployment process from a match is a later, separate capability.</summary>
public interface IDeployRecipe : IScopedDependency
{
    /// <summary>The recipe's stable name: what <c>recipe:</c> in <c>squid.yaml</c> and a user's choice refer to. Matched ignoring case.</summary>
    string Kind { get; }

    /// <summary>A score of 0 means the recipe does not apply; a higher score fits better. The reasons say why, for the person choosing.</summary>
    RecipeMatch Match(PackageFacts facts);
}

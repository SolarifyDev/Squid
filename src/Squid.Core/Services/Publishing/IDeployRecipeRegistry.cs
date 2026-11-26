using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

public interface IDeployRecipeRegistry : IScopedDependency
{
    /// <summary>
    /// The recipes that apply to the facts, best score first, each with its reasons; a recipe scoring 0 is left out. A recipe named by <c>squid.yaml</c> comes
    /// first whatever it scores; a name that matches no recipe is answered in <see cref="RecipeSelection.UnknownRecipe"/> with the valid names, not thrown.
    /// </summary>
    RecipeSelection Select(PackageFacts facts);
}

using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

public sealed class DeployRecipeRegistry : IDeployRecipeRegistry
{
    public const string ForcedReason = "Chosen by recipe: in squid.yaml";

    private readonly List<IDeployRecipe> _recipes;

    /// <summary>Throws when two recipes share a kind (ignoring case): a name that picks two recipes would make "recipe:" in squid.yaml ambiguous.</summary>
    public DeployRecipeRegistry(IEnumerable<IDeployRecipe> recipes)
    {
        _recipes = recipes.ToList();

        var duplicated = _recipes.GroupBy(r => r.Kind, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => $"'{g.Key}' ({string.Join(", ", g.Select(r => r.GetType().Name))})").ToList();

        if (duplicated.Count > 0)
            throw new InvalidOperationException($"Deploy recipe kinds must be unique, but these are registered more than once: {string.Join("; ", duplicated)}");
    }

    public RecipeSelection Select(PackageFacts facts)
    {
        var requested = facts.Get(PackageFactKeys.SquidRecipe);
        var selection = new RecipeSelection { RequestedKind = requested, ValidKinds = _recipes.Select(r => r.Kind).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList() };

        if (string.IsNullOrWhiteSpace(requested)) return WithScored(selection, facts, forced: null);

        var forced = _recipes.FirstOrDefault(r => string.Equals(r.Kind, requested.Trim(), StringComparison.OrdinalIgnoreCase));

        if (forced == null)
        {
            selection.UnknownRecipe = requested;
            return selection;
        }

        return WithScored(selection, facts, forced);
    }

    private RecipeSelection WithScored(RecipeSelection selection, PackageFacts facts, IDeployRecipe forced)
    {
        var scored = _recipes.Select(r => (Recipe: r, Match: r.Match(facts))).ToList();

        var others = scored.Where(s => s.Recipe != forced && s.Match.Score > 0).OrderByDescending(s => s.Match.Score).ThenBy(s => s.Recipe.Kind, StringComparer.OrdinalIgnoreCase);
        var first = scored.Where(s => s.Recipe == forced).Select(s => ToCandidate(s.Recipe, s.Match, ForcedReason));

        selection.Candidates = first.Concat(others.Select(s => ToCandidate(s.Recipe, s.Match, null))).ToList();

        return selection;
    }

    private static RecipeCandidate ToCandidate(IDeployRecipe recipe, RecipeMatch match, string extraReason)
    {
        var reasons = extraReason == null ? match.Reasons : new[] { extraReason }.Concat(match.Reasons).ToList();

        return new RecipeCandidate { Kind = recipe.Kind, Score = match.Score, Reasons = reasons };
    }
}

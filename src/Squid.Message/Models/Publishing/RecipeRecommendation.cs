namespace Squid.Message.Models.Publishing;

/// <summary>How well one recipe fits a package, and why. A <c>Score</c> of 0 means it does not apply.</summary>
public class RecipeMatch
{
    public int Score { get; set; }

    public IReadOnlyList<string> Reasons { get; set; } = Array.Empty<string>();
}

public class RecipeCandidate
{
    public string Kind { get; set; }

    public int Score { get; set; }

    public IReadOnlyList<string> Reasons { get; set; } = Array.Empty<string>();
}

/// <summary>
/// The recipes that fit a package, best first. When <c>squid.yaml</c> names a recipe, that recipe comes first (whatever its score) and the rest follow; when it
/// names one that does not exist, <see cref="UnknownRecipe"/> is that name, <see cref="Candidates"/> is empty and <see cref="ValidKinds"/> lists the real ones.
/// </summary>
public class RecipeSelection
{
    public List<RecipeCandidate> Candidates { get; set; } = new();

    public string RequestedKind { get; set; }

    public string UnknownRecipe { get; set; }

    public List<string> ValidKinds { get; set; } = new();
}

/// <summary>The recipes recommended for one stored package version, with the facts they were scored on. <c>Inspected</c> is false for a version pushed before inspection existed.</summary>
public class RecipeRecommendationsDto
{
    public bool Inspected { get; set; }

    public PackageFacts Facts { get; set; }

    public RecipeSelection Selection { get; set; } = new();
}

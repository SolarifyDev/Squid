using System.Linq;
using Squid.Core.Services.Publishing;
using Squid.Message.Models.Publishing;

namespace Squid.UnitTests.Services.Publishing;

public class DeployRecipeRegistryTests
{
    private static PackageFacts Facts(string recipe = null)
    {
        var facts = new PackageFacts();

        if (recipe != null) facts.Values[PackageFactKeys.SquidRecipe] = recipe;

        return facts;
    }

    private static DeployRecipeRegistry Registry(params IDeployRecipe[] recipes) => new(recipes);

    [Fact]
    public void Select_OrdersByScoreDescending_WithReasons()
    {
        var registry = Registry(new StubRecipe("low", 10, "weak signal"), new StubRecipe("high", 90, "strong", "very strong"), new StubRecipe("mid", 50));

        var selection = registry.Select(Facts());

        selection.Candidates.Select(c => c.Kind).ShouldBe(["high", "mid", "low"]);
        selection.Candidates[0].Reasons.ShouldBe(["strong", "very strong"]);
        selection.Candidates[0].Score.ShouldBe(90);
    }

    [Fact]
    public void Select_ScoreZero_IsNotApplicable_AndLeftOut()
    {
        var selection = Registry(new StubRecipe("fits", 5), new StubRecipe("nope", 0, "nothing here")).Select(Facts());

        selection.Candidates.Select(c => c.Kind).ShouldBe(["fits"]);
    }

    [Fact]
    public void Select_EqualScores_AreOrderedByKind_SoTheResultIsStable()
    {
        var selection = Registry(new StubRecipe("b", 5), new StubRecipe("a", 5)).Select(Facts());

        selection.Candidates.Select(c => c.Kind).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Select_NoRecipesRegistered_IsEmpty_NotAnError()
    {
        var selection = Registry().Select(Facts());

        selection.Candidates.ShouldBeEmpty();
        selection.UnknownRecipe.ShouldBeNull();
    }

    [Fact]
    public void Select_SquidYamlNamesAKnownRecipe_PutsItFirst_EvenAtScoreZero_IgnoringCase()
    {
        var registry = Registry(new StubRecipe("static", 90), new StubRecipe("docker", 0, "no dockerfile"), new StubRecipe("node", 40));

        var selection = registry.Select(Facts("DOCKER"));

        selection.Candidates.Select(c => c.Kind).ShouldBe(["docker", "static", "node"]);
        selection.Candidates[0].Reasons.ShouldBe([DeployRecipeRegistry.ForcedReason, "no dockerfile"]);
        selection.RequestedKind.ShouldBe("DOCKER");
        selection.UnknownRecipe.ShouldBeNull();
    }

    [Fact]
    public void Select_SquidYamlNamesAnUnknownRecipe_IsAResultListingValidNames_NotAnException()
    {
        var registry = Registry(new StubRecipe("static", 90), new StubRecipe("docker", 10));

        var selection = registry.Select(Facts("pythn"));

        selection.UnknownRecipe.ShouldBe("pythn");
        selection.ValidKinds.ShouldBe(["docker", "static"]);
        selection.Candidates.ShouldBeEmpty("an explicit but invalid choice must not silently fall back to detection");
    }

    [Fact]
    public void Select_BlankRecipeInSquidYaml_FallsBackToDetection()
    {
        var selection = Registry(new StubRecipe("static", 90)).Select(Facts("  "));

        selection.Candidates.Select(c => c.Kind).ShouldBe(["static"]);
        selection.UnknownRecipe.ShouldBeNull();
    }

    [Fact]
    public void Select_AlwaysListsTheValidKinds()
    {
        Registry(new StubRecipe("b", 1), new StubRecipe("a", 0)).Select(Facts()).ValidKinds.ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Select_ForcedRecipeThatAlsoScoresAboveZero_AppearsOnce()
    {
        var selection = Registry(new StubRecipe("static", 90, "has index.html"), new StubRecipe("docker", 40)).Select(Facts("static"));

        selection.Candidates.Select(c => c.Kind).ShouldBe(["static", "docker"]);
        selection.Candidates[0].Reasons.ShouldBe([DeployRecipeRegistry.ForcedReason, "has index.html"]);
    }

    [Fact]
    public void Construction_WithTwoRecipesOfOneKind_ThrowsNamingThem_IgnoringCase()
    {
        var ex = Should.Throw<InvalidOperationException>(() => Registry(new StubRecipe("Static", 1), new StubRecipe("docker", 1), new StubRecipe("static", 2)));

        ex.Message.ShouldContain("'Static'");
        ex.Message.ShouldContain(nameof(StubRecipe));
        ex.Message.ShouldNotContain("docker");
    }
}

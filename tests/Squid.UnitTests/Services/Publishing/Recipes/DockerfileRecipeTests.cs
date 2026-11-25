using System.Linq;
using Squid.Core.Services.Publishing.Exceptions;
using Squid.Core.Services.Publishing.Recipes.Dockerfile;
using Squid.Message.Models.Publishing;
using static Squid.UnitTests.Services.Publishing.Recipes.RecipeFacts;

namespace Squid.UnitTests.Services.Publishing.Recipes;

public class DockerfileRecipeTests
{
    private readonly DockerfileRecipe _recipe = new();

    [Fact]
    public void Match_Dockerfile_Fits_AndOutscoresStaticSite()
    {
        _recipe.Match(Of(Dockerfile())).Score.ShouldBe(DockerfileRecipe.FitsScore);
    }

    [Fact]
    public void Match_NoDockerfile_DoesNotApply()
    {
        _recipe.Match(Of(Static())).Score.ShouldBe(0);
    }

    [Fact]
    public async Task Render_BuildsAndRuns_AfterDeployPackage()
    {
        var plan = await _recipe.RenderAsync(Context(Of(Dockerfile(), (PackageFactKeys.DockerfileExposePort, "8080")), "Acme.Api", 9000), CancellationToken.None);

        plan.Steps.Select(s => s.Name).ShouldBe(["Deploy package", "Build and run"]);
        plan.Steps[0].Actions[0].Package.PackageId.ShouldBe("Acme.Api");

        var script = ScriptOf(plan);

        script.ShouldContain("HOST_PORT=9000");
        script.ShouldContain("CONTAINER_PORT=8080");
        script.ShouldContain("RELEASE_VERSION='#{Squid.Release.Number}'");
        script.ShouldContain("INSTALL_DIR='#{Squid.Action[Deploy package].Output.Squid.Action.Package.InstallationDirectoryPath}'");
        script.ShouldContain("docker build");
        script.ShouldNotContain("{{");
    }

    [Theory]
    [InlineData("3000", "8080", "3000")]
    [InlineData(null, "8080", "8080")]
    public async Task Render_ContainerPort_IsSquidYamlThenExpose(string runPort, string expose, string expected)
    {
        var facts = Of(Dockerfile(), (PackageFactKeys.DockerfileExposePort, expose));

        if (runPort != null) facts.Values[PackageFactKeys.SquidRunPort] = runPort;

        ScriptOf(await _recipe.RenderAsync(Context(facts), CancellationToken.None)).ShouldContain($"CONTAINER_PORT={expected}");
    }

    [Fact]
    public async Task Render_NoPortAnywhere_AsksForOne()
    {
        var ex = await Should.ThrowAsync<RecipeInputInvalidException>(() => _recipe.RenderAsync(Context(Of(Dockerfile())), CancellationToken.None));

        ex.Input.ShouldBe("containerPort");
        ex.Message.ShouldContain("squid.yaml");
        ex.Message.ShouldContain("EXPOSE");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("-1")]
    public async Task Render_NonsensePort_IsInvalid(string port)
    {
        await Should.ThrowAsync<RecipeInputInvalidException>(() => _recipe.RenderAsync(Context(Of(Dockerfile(), (PackageFactKeys.SquidRunPort, port))), CancellationToken.None));
    }

    [Fact]
    public async Task Render_WrappedArchive_BuildsInsideTheRootFolder()
    {
        var script = ScriptOf(await _recipe.RenderAsync(Context(Of(Dockerfile(), Root("app"), (PackageFactKeys.DockerfileExposePort, "80"))), CancellationToken.None));

        script.ShouldContain("BUILD_SUB_PATH='app'");
    }

    [Fact]
    public async Task Render_HostileAppName_NeverReachesTheScriptUnquoted()
    {
        var script = ScriptOf(await _recipe.RenderAsync(Context(Of(Dockerfile(), (PackageFactKeys.DockerfileExposePort, "80")), "a'; rm -rf / #"), CancellationToken.None));

        script.ShouldContain("APP_NAME='a'\\''; rm -rf / #'");
        script.ShouldContain("CONTAINER_NAME='squid-app-a-rm-rf'");
    }
}

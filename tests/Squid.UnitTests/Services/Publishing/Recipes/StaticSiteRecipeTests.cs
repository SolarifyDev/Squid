using System.Linq;
using Squid.Core.Services.Publishing;
using Squid.Core.Services.Publishing.Exceptions;
using Squid.Core.Services.Publishing.Recipes;
using Squid.Core.Services.Publishing.Recipes.Dockerfile;
using Squid.Core.Services.Publishing.Recipes.StaticSite;
using Squid.Message.Models.Publishing;
using static Squid.UnitTests.Services.Publishing.Recipes.RecipeFacts;

namespace Squid.UnitTests.Services.Publishing.Recipes;

public class StaticSiteRecipeTests
{
    private readonly StaticSiteRecipe _recipe = new();

    [Fact]
    public void Match_IndexHtmlOnly_FitsWell()
    {
        var match = _recipe.Match(Of(Static(), StaticDir(".")));

        match.Score.ShouldBe(StaticSiteRecipe.FitsScore);
        match.Reasons.ShouldContain(r => r.Contains("index.html"));
    }

    [Fact]
    public void Match_NoIndexHtml_DoesNotApply()
    {
        _recipe.Match(Of(Dockerfile())).Score.ShouldBe(0);
        _recipe.Match(Of()).Score.ShouldBe(0);
    }

    [Fact]
    public void Match_StaticAndDockerfile_ScoresLow_SoTheDockerfileRecipeWins()
    {
        var facts = Of(Static(), StaticDir("."), Dockerfile());

        _recipe.Match(facts).Score.ShouldBe(StaticSiteRecipe.DockerfilePresentScore);
        _recipe.Match(facts).Score.ShouldBeLessThan(new DockerfileRecipe().Match(facts).Score);
    }

    [Fact]
    public void Match_NamesTheFolderTheIndexIsIn()
    {
        _recipe.Match(Of(Static(), StaticDir("dist"))).Reasons.ShouldContain(r => r.Contains("'dist'"));
    }

    [Fact]
    public async Task Render_MakesDeployPackageThenServeSteps_OnTheRequestedRoles()
    {
        var plan = await _recipe.RenderAsync(Context(Of(Static(), StaticDir(".")), "Acme.Web", "web", "edge"), CancellationToken.None);

        plan.Steps.Select(s => s.Name).ShouldBe([ContainerAppPlan.DeployPackageStepName, "Serve site"]);
        plan.Steps.ShouldAllBe(s => s.TargetRoles.SequenceEqual(new[] { "web", "edge" }));
        plan.Inputs.Select(i => i.Name).ShouldBe([PlanInputNames.HostPort]);

        var deploy = plan.Steps[0].Actions.Single();

        deploy.ActionType.ShouldBe("Squid.TentaclePackage");
        deploy.Name.ShouldBe(ContainerAppPlan.DeployPackageStepName);
        deploy.Package.PackageId.ShouldBe("Acme.Web");
        deploy.Properties["Squid.Action.Package.InstallationDirectoryMode"].ShouldBe("Versioned");

        var serve = plan.Steps[1].Actions.Single();

        serve.ActionType.ShouldBe("Squid.Script");
        serve.Package.ShouldBeNull();
        serve.Properties["Squid.Action.Script.Syntax"].ShouldBe("Bash");
    }

    [Fact]
    public async Task Render_ScriptReadsTheExtractionDirectoryFromTheDeployPackageOutputVariable()
    {
        var plan = await _recipe.RenderAsync(Context(Of(Static(), StaticDir("."))), CancellationToken.None);

        ScriptOf(plan).ShouldContain("read_squid_var INSTALL_DIR \"the folder the package was extracted into\" '#{Squid.Action[Deploy package].Output.Squid.Action.Package.InstallationDirectoryPath | ToBase64}'");
    }

    [Theory]
    [InlineData(null, ".", "", "''")]
    [InlineData("site", ".", "site", "'site'")]
    [InlineData(null, "dist", "dist", "'dist'")]
    [InlineData("site", "build", "site/build", "'site/build'")]
    public async Task Render_SitePath_IsTheArchiveRootThenTheIndexFolder(string root, string indexDir, string expectedPath, string expectedLiteral)
    {
        var facts = root == null ? Of(Static(), StaticDir(indexDir)) : Of(Static(), StaticDir(indexDir), Root(root));

        var plan = await _recipe.RenderAsync(Context(facts), CancellationToken.None);

        ContainerAppPlan.SubPath(facts, indexDir).ShouldBe(expectedPath);
        ScriptOf(plan).ShouldContain($"SITE_SUB_PATH={expectedLiteral}");
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("/healthz", "/healthz")]
    [InlineData("status", "/status")]
    public async Task Render_HealthPath_IsFromSquidYaml_ElseRoot(string declared, string expected)
    {
        var facts = declared == null ? Of(Static(), StaticDir(".")) : Of(Static(), StaticDir("."), (PackageFactKeys.SquidHealthPath, declared));

        var plan = await _recipe.RenderAsync(Context(facts), CancellationToken.None);

        ScriptOf(plan).ShouldContain($"HEALTH_PATH='{expected}'");
    }

    [Fact]
    public async Task Render_ServerIsPinned_PortComesFromTheEnvironmentsVariable_AndNoPlaceholderIsLeft()
    {
        var plan = await _recipe.RenderAsync(Context(Of(Static(), StaticDir("."))), CancellationToken.None);
        var script = ScriptOf(plan);

        script.ShouldContain("read_squid_var HOST_PORT \"the host port\" '#{Squid.Publish.Input.hostPort | ToBase64}'");
        script.ShouldNotContain("HOST_PORT=");
        script.ShouldContain("CONTAINER_PORT=80");
        script.ShouldContain("SERVER_IMAGE='caddy:2.11.9'");
        StaticSiteRecipe.ServerImage.ShouldMatch(@"^caddy:\d+\.\d+\.\d+$", "an exact version, never a floating tag");
        script.ShouldContain($"HEALTH_TIMEOUT_SECONDS={ContainerAppPlan.HealthTimeoutSeconds}");
        script.ShouldContain("CONTAINER_NAME_BASE='squid-acme-web'");
        script.ShouldContain("'#{Squid.Project.Id | ToBase64}'");
        script.ShouldContain("'#{Squid.Environment.Id | ToBase64}'");
        script.ShouldNotMatch(@"\{\{\w+\}\}");
    }

    [Fact]
    public async Task Render_HostileFactsAndNames_AreQuotedAsLiterals()
    {
        var facts = Of(Static(), StaticDir("."), Root("$(touch pwned);`id`\"'x"), (PackageFactKeys.SquidHealthPath, "/a;b"));

        var plan = await _recipe.RenderAsync(Context(facts, "Evil'$(id)"), CancellationToken.None);
        var script = ScriptOf(plan);

        script.ShouldContain("SITE_SUB_PATH='$(touch pwned);`id`\"'\\''x'");
        script.ShouldContain("APP_NAME='Evil'\\''$(id)'");
        script.ShouldContain("HEALTH_PATH='/a;b'");
        script.ShouldContain("CONTAINER_NAME_BASE='squid-evil-id'");
    }

    [Fact]
    public async Task Render_ValueThatLooksLikeAVariable_IsRefused()
    {
        var facts = Of(Static(), StaticDir("."), Root("#{Squid.Secret}"));

        var ex = await Should.ThrowAsync<RecipeInputInvalidException>(() => _recipe.RenderAsync(Context(facts), CancellationToken.None));

        ex.Message.ShouldContain("#{");
    }

    [Fact]
    public async Task Render_ValueThatLooksLikeAPlaceholder_IsNotSubstitutedAgain()
    {
        var facts = Of(Static(), StaticDir("."), Root("{{HostPort}}"));

        var script = ScriptOf(await _recipe.RenderAsync(Context(facts, "Acme"), CancellationToken.None));

        script.ShouldContain("SITE_SUB_PATH='{{HostPort}}'");
    }

    [Fact]
    public async Task Render_HealthPathWithWhitespace_IsInvalid()
    {
        var facts = Of(Static(), StaticDir("."), (PackageFactKeys.SquidHealthPath, "/a b"));

        await Should.ThrowAsync<RecipeInputInvalidException>(() => _recipe.RenderAsync(Context(facts), CancellationToken.None));
    }
}

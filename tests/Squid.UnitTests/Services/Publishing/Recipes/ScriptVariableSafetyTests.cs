using System.Diagnostics;
using System.IO;
using System.Linq;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Publishing;
using Squid.Core.Services.Publishing.Recipes;
using Squid.Core.Services.Publishing.Recipes.Dockerfile;
using Squid.Core.Services.Publishing.Recipes.StaticSite;
using Squid.Core.VariableSubstitution;
using Squid.Message.Constants;
using Squid.Message.Models.Publishing;
using static Squid.UnitTests.Services.Publishing.Recipes.RecipeFacts;

namespace Squid.UnitTests.Services.Publishing.Recipes;

/// <summary>
/// The deployment substitutes <c>#{...}</c> as plain text, so a project name, an environment name or a release number must never reach a script inside a shell literal.
/// These render the real scripts, run them through the real variable expansion with hostile values, and run the result under real bash (with no docker on the PATH, so a
/// script that gets as far as using docker stops with its message): the hostile text must never execute.
/// </summary>
public class ScriptVariableSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"squid-ut-safety-{Guid.NewGuid():N}");

    public ScriptVariableSafetyTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    public static IEnumerable<object[]> HostileValues() =>
    [
        ["Joe's site"],
        ["x'$(touch PWNED)'"],
        ["`touch PWNED`"],
        ["$(touch PWNED)"],
        ["a\"; touch PWNED; \""],
        ["line one\nline two; touch PWNED"],
        ["with space and $HOME and ${HOME} and \\ and ' and #"],
        ["';touch PWNED;'"]
    ];

    private string Marker => Path.Combine(_root, "PWNED");

    private static bool HasBash => File.Exists("/bin/bash");

    private async Task<string> ScriptAsync(IDeployRecipe recipe, PackageFacts facts, Dictionary<string, string> variables)
    {
        var plan = await recipe.RenderAsync(Context(facts), CancellationToken.None);
        var dictionary = new VariableDictionary();

        foreach (var (name, value) in variables) dictionary.Set(name, value);

        return VariableExpander.ExpandString(ScriptOf(plan), dictionary);
    }

    private static Dictionary<string, string> Variables(string installDir = "/opt/app", string project = "7", string environment = "2", string port = "8080", string release = "1.0.0")
    {
        return new Dictionary<string, string>
        {
            [ContainerAppPlan.InstallDirVariable] = installDir,
            [SpecialVariables.Project.Id] = project,
            [SpecialVariables.Environment.Id] = environment,
            [PlanInputVariables.Name(PlanInputNames.HostPort)] = port,
            [SpecialVariables.Release.Number] = release
        };
    }

    private (int ExitCode, string Output) RunWithoutDocker(string script)
    {
        // A PATH with the tools the prelude needs and no docker.
        var emptyPath = Path.Combine(_root, "bin");

        Directory.CreateDirectory(emptyPath);

        foreach (var tool in new[] { "base64", "tr", "cut", "sed", "head", "wc", "touch" })
        {
            var source = new[] { "/usr/bin", "/bin" }.Select(d => Path.Combine(d, tool)).First(File.Exists);

            if (!File.Exists(Path.Combine(emptyPath, tool))) File.CreateSymbolicLink(Path.Combine(emptyPath, tool), source);
        }

        var path = Path.Combine(_root, "script.sh");

        File.WriteAllText(path, script);

        var info = new ProcessStartInfo("/bin/bash") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _root };

        info.ArgumentList.Add(path);
        info.Environment["PATH"] = emptyPath;

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

        process.WaitForExit();

        return (process.ExitCode, output);
    }

    private static PackageFacts StaticFacts() => Of(Static(), StaticDir("."));

    private static PackageFacts DockerFacts() => Of(Dockerfile(), (PackageFactKeys.DockerfileExposePort, "8080"));

    [Fact]
    public async Task EveryVariableExpressionInAScript_IsAnEncodedOne_AndNothingElseLooksLikeOne()
    {
        foreach (var (recipe, facts) in new (IDeployRecipe, PackageFacts)[] { (new StaticSiteRecipe(), StaticFacts()), (new DockerfileRecipe(), DockerFacts()) })
        {
            var script = ScriptOf(await recipe.RenderAsync(Context(facts), CancellationToken.None));
            var expressions = System.Text.RegularExpressions.Regex.Matches(script, "#\\{");

            expressions.Count.ShouldBeGreaterThan(0);
            System.Text.RegularExpressions.Regex.Matches(script, "'#\\{[^}']+ \\| ToBase64\\}'").Count.ShouldBe(expressions.Count, "a stray #{ (even in a comment) makes the deployment's substitution fail or inject; each must be an encoded expression inside quotes");
        }
    }

    [Theory]
    [MemberData(nameof(HostileValues))]
    public async Task HostileExtractionFolder_NeverExecutes_AndTheScriptIsStillValidBash(string hostile)
    {
        if (!HasBash) return;

        foreach (var (recipe, facts) in new (IDeployRecipe, PackageFacts)[] { (new StaticSiteRecipe(), StaticFacts()), (new DockerfileRecipe(), DockerFacts()) })
        {
            var script = await ScriptAsync(recipe, facts, Variables(installDir: $"/srv/{hostile}/1.0.0"));

            script.ShouldNotContain("touch PWNED", Case.Sensitive, "the hostile text must not appear in the script at all, only its Base64");

            var run = RunWithoutDocker(script);

            File.Exists(Marker).ShouldBeFalse();
            run.Output.ShouldContain("Docker is required on this target", Case.Sensitive, $"the script must get past reading its variables to the docker check\n{run.Output}");
        }
    }

    [Theory]
    [MemberData(nameof(HostileValues))]
    public async Task HostileReleaseNumber_NeverExecutes(string hostile)
    {
        if (!HasBash) return;

        var script = await ScriptAsync(new DockerfileRecipe(), DockerFacts(), Variables(release: hostile));
        var run = RunWithoutDocker(script);

        File.Exists(Marker).ShouldBeFalse();
        script.ShouldNotContain("PWNED", Case.Sensitive);
        run.Output.ShouldContain("Docker is required on this target", Case.Sensitive, run.Output);
    }

    [Theory]
    [InlineData("7'; touch PWNED; '")]
    [InlineData("$(touch PWNED)")]
    [InlineData("")]
    [InlineData("abc")]
    public async Task ANonNumericProjectOrEnvironmentId_StopsTheScript_BeforeAnythingElse(string hostile)
    {
        if (!HasBash) return;

        var script = await ScriptAsync(new StaticSiteRecipe(), StaticFacts(), Variables(environment: hostile));
        var run = RunWithoutDocker(script);

        run.ExitCode.ShouldNotBe(0);
        File.Exists(Marker).ShouldBeFalse();
        run.Output.ShouldMatch("not a number|did not give this script");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("80; touch PWNED")]
    [InlineData("-1")]
    public async Task ABadHostPortVariable_StopsTheScript(string port)
    {
        if (!HasBash) return;

        var run = RunWithoutDocker(await ScriptAsync(new StaticSiteRecipe(), StaticFacts(), Variables(port: port)));

        run.ExitCode.ShouldNotBe(0);
        File.Exists(Marker).ShouldBeFalse();
        run.Output.ShouldContain("TCP port");
    }

    [Fact]
    public async Task AValueThatIsNotBase64_StopsTheScript_WithAMessageNamingIt()
    {
        if (!HasBash) return;

        var script = await ScriptAsync(new StaticSiteRecipe(), StaticFacts(), Variables());
        var tampered = System.Text.RegularExpressions.Regex.Replace(script, "(read_squid_var HOST_PORT \"the host port\" ).*", "$1'80 !not-base64'");

        tampered.ShouldNotBe(script, "the line to tamper with must exist");

        var run = RunWithoutDocker(tampered);

        run.ExitCode.ShouldNotBe(0);
        run.Output.ShouldContain("The deployment did not give this script the host port.");
        File.Exists(Marker).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheHealthProbe_TurnsOffUrlGlobbing_SoBracketsInTheHealthPathAreLiteral(bool dockerfile)
    {
        var recipe = dockerfile ? (IDeployRecipe)new DockerfileRecipe() : new StaticSiteRecipe();
        var script = ScriptOf(await recipe.RenderAsync(Context(dockerfile ? DockerFacts() : StaticFacts()), CancellationToken.None));

        script.ShouldMatch(@"(?m)^\s*curl -g -fsS .*""\$1""", "curl treats [..] and {..} in a URL as ranges unless -g is given, so a health path containing them would never be probed as written");
    }

    [Fact]
    public async Task AVariableTheDeploymentDidNotDefine_StopsTheScript_WithAMessageNamingIt()
    {
        if (!HasBash) return;

        var variables = Variables();

        variables.Remove(PlanInputVariables.Name(PlanInputNames.HostPort));

        var run = RunWithoutDocker(await ScriptAsync(new StaticSiteRecipe(), StaticFacts(), variables));

        run.ExitCode.ShouldNotBe(0);
        run.Output.ShouldContain("the host port");
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0+build.5", "1.0.0-build.5")]
    [InlineData(".hidden", "v.hidden")]
    [InlineData("-dash", "v-dash")]
    [InlineData("release candidate/2", "release-candidate-2")]
    [InlineData("", "v")]
    public async Task TheImageTag_IsADockerTag_WhateverTheReleaseNumberIs(string release, string expected)
    {
        if (!HasBash) return;

        var plan = await new DockerfileRecipe().RenderAsync(Context(DockerFacts()), CancellationToken.None);
        var script = ScriptOf(plan);
        var start = script.IndexOf("docker_tag() {", StringComparison.Ordinal);
        var end = script.IndexOf("prepare_image() {", StringComparison.Ordinal);
        var function = script[start..end];

        var info = new ProcessStartInfo("/bin/bash") { RedirectStandardOutput = true };

        info.ArgumentList.Add("-c");
        info.ArgumentList.Add($"{function}\ndocker_tag \"$1\"");
        info.ArgumentList.Add("bash");
        info.ArgumentList.Add(release);

        using var process = Process.Start(info)!;
        var tag = process.StandardOutput.ReadToEnd();

        process.WaitForExit();

        tag.ShouldBe(expected);
    }

    [Fact]
    public async Task TheImageTag_IsCappedAt128Characters_AndNeverStartsWithADotOrDash()
    {
        if (!HasBash) return;

        var plan = await new DockerfileRecipe().RenderAsync(Context(DockerFacts()), CancellationToken.None);
        var script = ScriptOf(plan);
        var function = script[script.IndexOf("docker_tag() {", StringComparison.Ordinal)..script.IndexOf("prepare_image() {", StringComparison.Ordinal)];

        var info = new ProcessStartInfo("/bin/bash") { RedirectStandardOutput = true };

        info.ArgumentList.Add("-c");
        info.ArgumentList.Add($"{function}\ndocker_tag \"$1\"");
        info.ArgumentList.Add("bash");
        info.ArgumentList.Add("-" + new string('9', 300));

        using var process = Process.Start(info)!;
        var tag = process.StandardOutput.ReadToEnd();

        process.WaitForExit();

        tag.Length.ShouldBe(128);
        tag.ShouldMatch("^[A-Za-z0-9_][A-Za-z0-9_.-]*$");
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Publishing;
using Squid.Core.Services.Publishing.Inspectors;
using Squid.Core.Services.Publishing.Recipes;
using Squid.Core.Services.Publishing.Recipes.Dockerfile;
using Squid.Core.Services.Publishing.Recipes.StaticSite;
using Squid.Core.VariableSubstitution;
using Squid.Message.Models.Publishing;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Publishing;

/// <summary>
/// Fidelity tier: HIGH. The archive is a real zip, inspected by the real inspection service with the real inspectors; the real recipe renders the real embedded
/// bash script; the deployment's own variable substitution fills it in; and that exact script runs under real bash against the real Docker daemon on this machine
/// (the target's side of a deployment). Only the Tentacle hop is left out: the extraction folder the deploy-package step would publish is made by extracting the
/// same zip into a temp folder. The static site's server is the real <c>caddy:2</c> when the machine has it or can pull it; on a machine that can do neither (offline) a
/// stand-in image built from busybox, tagged with a unique name and removed afterwards, answers to the same <c>caddy file-server --root --listen</c> command line, so the
/// script, the mount and the swap are still exercised but caddy itself is not. Every test is skipped (returns) when there is no Docker daemon, no bash, or the OS is Windows.
/// </summary>
[Trait("Category", "PublishRecipeDockerE2E")]
public class PublishRecipeDockerE2ETests
{
    private const string BuiltImage = "busybox";

    private static readonly Lazy<bool> DockerUsable = new(ProbeDocker);

    private static bool ProbeDocker()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash")) return false;

        var probe = Run("/bin/bash", ["-c", "docker info >/dev/null 2>&1"], null, TimeSpan.FromSeconds(30));

        return probe.ExitCode == 0;
    }

    [Fact]
    public async Task StaticSite_FromARealWrappedArchive_IsServedOnTheHostPort()
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        await app.UseServerImageAsync();

        var run = await app.DeployAsync(new StaticSiteRecipe(), [("mysite/dist/index.html", "<h1>hello from the static site</h1>"), ("mysite/dist/about.html", "about")]);

        run.ExitCode.ShouldBe(0, run.Describe($"the static site deploy should succeed; inspect with: docker ps -a --filter name={app.ContainerName}"));
        (await app.GetAsync("/")).ShouldBe("<h1>hello from the static site</h1>");
        (await app.GetAsync("/about.html")).ShouldBe("about");
        app.ContainersOf().ShouldBe([app.ContainerName], "only the live container may remain: no candidate, no previous");
    }

    [Fact]
    public async Task DockerfileApp_IsBuiltFromTheArchive_AndServed_OnTheHostPort()
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        var run = await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-one"));

        run.ExitCode.ShouldBe(0, run.Describe($"the Dockerfile app deploy should succeed; inspect with: docker ps -a --filter name={app.ContainerName}"));
        (await app.GetAsync("/")).ShouldBe("version-one");
        app.ContainersOf().ShouldBe([app.ContainerName]);
        app.ImageTags().ShouldContain("1.0.0", "the image is tagged with the release number");
    }

    [Fact]
    public async Task SecondVersion_ReplacesTheFirst_OnTheSamePort_AndLeavesNoOldContainer()
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        (await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-one"))).ExitCode.ShouldBe(0);

        var second = await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-two"), version: "2.0.0");

        second.ExitCode.ShouldBe(0, second.Describe("the second version should replace the first"));
        (await app.GetAsync("/")).ShouldBe("version-two");
        app.ContainersOf().ShouldBe([app.ContainerName], "the previous container must be removed once the new one is healthy");
    }

    [Fact]
    public async Task SecondVersionThatCrashes_FailsTheDeployment_AndTheFirstKeepsServing()
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        (await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-one"))).ExitCode.ShouldBe(0);

        var failed = await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-two", command: "[\"sh\",\"-c\",\"echo boom-from-the-new-version; exit 1\"]"), version: "2.0.0");

        failed.ExitCode.ShouldNotBe(0, failed.Describe("a version that crashes must fail the deployment"));
        failed.Output.ShouldContain("boom-from-the-new-version", Case.Sensitive, failed.Describe("the new container's last log lines must be printed"));
        (await app.GetAsync("/")).ShouldBe("version-one", "the old version must still be online");
        app.ContainersOf().ShouldBe([app.ContainerName], "the failed candidate must be removed");
    }

    [Fact]
    public async Task SecondVersionThatNeverBecomesHealthy_FailsTheDeployment_AndTheFirstKeepsServing()
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        (await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-one"))).ExitCode.ShouldBe(0);

        var failed = await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-two", healthPath: "/never-served"), version: "2.0.0", healthTimeoutSeconds: 4);

        failed.ExitCode.ShouldNotBe(0, failed.Describe("a version whose health check never passes must fail the deployment"));
        failed.Output.ShouldContain("did not start, or did not answer", Case.Sensitive, failed.Describe("the failure must say what was checked"));
        (await app.GetAsync("/")).ShouldBe("version-one");
        app.ContainersOf().ShouldBe([app.ContainerName]);
    }

    [Fact]
    public async Task NewVersionThatCannotTakeTheNewPort_RestoresThePreviousOne()
    {
        if (!DockerUsable.Value) return;

        using var occupier = new PublishedApp();
        using var app = new PublishedApp();

        (await occupier.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("occupier"))).ExitCode.ShouldBe(0);
        (await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-one"))).ExitCode.ShouldBe(0);

        var previousPort = app.HostPort;

        app.HostPort = occupier.HostPort;

        var failed = await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-two"), version: "2.0.0");

        failed.ExitCode.ShouldNotBe(0, failed.Describe("taking a port another container holds must fail the deployment"));
        failed.Output.ShouldContain("restored", Case.Sensitive, failed.Describe("the output must say the previous version was restored"));
        (await PublishedApp.GetAsync(previousPort, "/")).ShouldBe("version-one", "the previous version must be running again on its own port");
        (await occupier.GetAsync("/")).ShouldBe("occupier");
        app.ContainersOf().ShouldBe([app.ContainerName]);
    }

    public static TheoryData<string> HostileNames => new()
    {
        "Joe's site",
        "x'$(touch PWNED)'",
        "back`touch PWNED`tick",
        "two\nlines; touch PWNED",
        "spaces and \"quotes\" and $HOME"
    };

    [Theory]
    [MemberData(nameof(HostileNames))]
    public async Task AHostileProjectOrEnvironmentNameInTheExtractionPath_NeverExecutes_AndTheDeployWorks(string hostile)
    {
        if (!DockerUsable.Value) return;

        // The extraction folder holds the project and environment names; the deployment hands the script that path through its own variable substitution.
        using var site = new PublishedApp();
        using var docker = new PublishedApp();

        await site.UseServerImageAsync();

        var siteRun = await site.DeployAsync(new StaticSiteRecipe(), [("index.html", "<h1>hostile static</h1>")], installParent: Path.Combine(site.Root, hostile));
        var dockerRun = await docker.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("hostile docker"), installParent: Path.Combine(docker.Root, hostile));

        siteRun.ExitCode.ShouldBe(0, siteRun.Describe("a path with shell syntax in it must not break the static site deploy"));
        dockerRun.ExitCode.ShouldBe(0, dockerRun.Describe("a path with shell syntax in it must not break the Dockerfile deploy"));
        (await site.GetAsync("/")).ShouldBe("<h1>hostile static</h1>");
        (await docker.GetAsync("/")).ShouldBe("hostile docker");
        File.Exists(Path.Combine(site.Root, "PWNED")).ShouldBeFalse("the hostile text ran as a command");
        File.Exists(Path.Combine(docker.Root, "PWNED")).ShouldBeFalse("the hostile text ran as a command");
    }

    [Theory]
    [InlineData("1.0'$(touch PWNED)'")]
    [InlineData("1.0\"; touch PWNED; \"")]
    [InlineData("`touch PWNED`")]
    [InlineData(".leading-dot")]
    [InlineData("-leading-dash")]
    public async Task AHostileReleaseNumber_NeverExecutes_AndTheImageGetsAValidTag(string release)
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        var run = await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("x"), version: release);

        run.ExitCode.ShouldBe(0, run.Describe("a release number with shell syntax in it must not break the deploy"));
        File.Exists(Path.Combine(app.Root, "PWNED")).ShouldBeFalse("the release number ran as a command");

        var tag = app.ImageTags().Single();

        tag.ShouldMatch("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$");
        (await app.GetAsync("/")).ShouldBe("x");
    }

    [Fact]
    public async Task AfterEachSuccessfulSwap_OnlyTheRunningAndThePreviousImageAreKept()
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        foreach (var version in new[] { "1.0.0", "2.0.0", "3.0.0" })
            (await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp($"content-{version}"), version: version)).ExitCode.ShouldBe(0);

        app.ImageTags().Order().ToArray().ShouldBe(["2.0.0", "3.0.0"], "after three releases the first one's image must be gone, the previous one kept for a rollback");
        (await app.GetAsync("/")).ShouldBe("content-3.0.0");
    }

    [Fact]
    public async Task AFailedDeploy_NeverRemovesAnImage()
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        (await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("one"), version: "1.0.0")).ExitCode.ShouldBe(0);
        (await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("two", command: "[\"sh\",\"-c\",\"exit 1\"]"), version: "2.0.0")).ExitCode.ShouldNotBe(0);

        app.ImageTags().Order().ToArray().ShouldBe(["1.0.0", "2.0.0"]);
    }

    [Fact]
    public async Task KilledOutrightAfterTheRunningVersionWasStopped_TheNextDeployStartsItAgain_BeforeAnythingElse()
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        (await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-one"))).ExitCode.ShouldBe(0);

        var running = await app.StartAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-two"), app.NewShim(s => s.PauseAfter = "stop"), "2.0.0");

        await running.WaitUntilPausedAsync();
        app.IsRunning(app.ContainerName).ShouldBeFalse("the old version was stopped, and the script has not yet renamed it");
        running.Kill();

        app.ContainersOf().ShouldBe([app.ContainerName], "killed between stopping and renaming: the old container is left stopped under the live name");
        app.IsRunning(app.ContainerName).ShouldBeFalse();

        var next = await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-three"), version: "3.0.0");

        next.ExitCode.ShouldBe(0, next.Describe("the next deploy must recover and then deploy"));
        next.Output.ShouldContain("found stopped", Case.Sensitive, next.Describe("recovery must start the stopped live container first"));
        (await app.GetAsync("/")).ShouldBe("version-three");
        app.ContainersOf().ShouldBe([app.ContainerName]);
    }

    [Fact]
    public async Task KilledOutrightAfterTheRunningVersionWasStopped_AndTheNextDeployFails_TheOldVersionIsServingAgain()
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        (await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-one"))).ExitCode.ShouldBe(0);

        var running = await app.StartAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-two"), app.NewShim(s => s.PauseAfter = "stop"), "2.0.0");

        await running.WaitUntilPausedAsync();
        running.Kill();

        var next = await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-three", command: "[\"sh\",\"-c\",\"exit 1\"]"), version: "3.0.0");

        next.ExitCode.ShouldNotBe(0);
        (await app.GetAsync("/")).ShouldBe("version-one", "recovery brought the stopped version back even though the new deploy then failed");
    }

    [Fact]
    public async Task StoppedAfterTheRunningVersionWasStopped_ThePreviousOneIsRestored_ByTheTrap()
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        (await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-one"))).ExitCode.ShouldBe(0);

        var running = await app.StartAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-two"), app.NewShim(s => s.PauseAfter = "stop"), "2.0.0");

        await running.WaitUntilPausedAsync();

        var ended = running.Terminate();

        ended.ExitCode.ShouldBe(143, ended.Describe("SIGTERM ends the script with 143 after its trap ran"));
        ended.Output.ShouldContain("restoring the previous version", Case.Sensitive, ended.Describe("the trap must say it put the old version back"));
        (await app.GetAsync("/")).ShouldBe("version-one");
        app.ContainersOf().ShouldBe([app.ContainerName]);
    }

    [Fact]
    public async Task StoppedWhileWaitingForTheNewVersionOnTheRealPort_ThePreviousOneIsRestored_AndTheNewOneRemoved()
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        (await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-one"))).ExitCode.ShouldBe(0);

        var running = await app.StartAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-two"), app.NewShim(s => { s.PauseAfter = "run"; s.PauseNth = 2; }), "2.0.0");

        await running.WaitUntilPausedAsync();
        app.ContainersOf().ShouldBe([app.ContainerName, app.ContainerName + "-previous"], "mid-swap: the new container is live and the old one is kept stopped as -previous");

        var ended = running.Terminate();

        ended.ExitCode.ShouldBe(143);
        (await app.GetAsync("/")).ShouldBe("version-one");
        app.ContainersOf().ShouldBe([app.ContainerName], "the new container and -previous are both gone: the old one is back under the live name");
    }

    [Fact]
    public async Task KilledOutrightWhileTheNewVersionIsHealthy_TheNextDeployConfirmsItBeforeRemovingThePreviousOne()
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        (await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-one"))).ExitCode.ShouldBe(0);

        var running = await app.StartAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-two"), app.NewShim(s => { s.PauseAfter = "run"; s.PauseNth = 2; }), "2.0.0");

        await running.WaitUntilPausedAsync();
        running.Kill();

        app.ContainersOf().ShouldBe([app.ContainerName, app.ContainerName + "-previous"], "killed mid-swap: both are left");

        var next = await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-three"), version: "3.0.0");

        next.ExitCode.ShouldBe(0, next.Describe("the next deploy settles the interrupted swap and goes on"));
        (await app.GetAsync("/")).ShouldBe("version-three");
        app.ContainersOf().ShouldBe([app.ContainerName]);
    }

    [Fact]
    public async Task KilledOutrightWhileTheNewVersionIsNotHealthy_TheNextDeployRestoresThePreviousOne_NeverDeletingTheLastKnownGood()
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        (await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-one"))).ExitCode.ShouldBe(0);

        // The new version starts on the real port and is then stopped, so it is live but dead; the script is killed while it waits for it.
        var running = await app.StartAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-two"), app.NewShim(s => { s.StopAfterRun = 2; s.PauseAfter = "run"; s.PauseNth = 2; }), "2.0.0");

        await running.WaitUntilPausedAsync();
        running.Kill();

        app.ContainersOf().ShouldBe([app.ContainerName, app.ContainerName + "-previous"]);

        // The next deploy fails at its own candidate check; what matters is that recovery already put the last known-good version back.
        var next = await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-three", command: "[\"sh\",\"-c\",\"exit 1\"]"), version: "3.0.0");

        next.ExitCode.ShouldNotBe(0);
        (await app.GetAsync("/")).ShouldBe("version-one", "the previous container was the last known-good and must have been restored, not deleted");
    }

    [Fact]
    public async Task ANewVersionThatPassesTheCandidateCheckButDiesOnTheRealPort_IsReplacedByThePreviousOne()
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        (await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-one"))).ExitCode.ShouldBe(0);

        // The shim stops the container the second docker run (the real-port one) started: the candidate was fine, the real one is not.
        var running = await app.StartAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("version-two"), app.NewShim(s => s.StopAfterRun = 2), "2.0.0");
        var ended = await running.WaitForExitAsync();

        ended.ExitCode.ShouldNotBe(0, ended.Describe("a new version that dies on the real port must fail the deployment"));
        ended.Output.ShouldContain("Any previous version is being restored", Case.Sensitive, ended.Describe("the real-port health check must have noticed"));
        (await app.GetAsync("/")).ShouldBe("version-one", "the previous version is serving again");
        app.ContainersOf().ShouldBe([app.ContainerName]);
    }

    [Fact]
    public async Task ScriptWithoutTheDeploymentIds_RefusesToRun()
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        var run = await app.DeployAsync(new DockerfileRecipe(), PublishedApp.DockerfileApp("x"), keepIdTokens: true);

        run.ExitCode.ShouldNotBe(0);
        run.Output.ShouldContain("did not give this script", Case.Sensitive, run.Describe("a script whose ids were not substituted must stop before touching docker"));
    }

    [Fact]
    public async Task NoDocker_FailsEarly_WithAClearMessage()
    {
        if (!DockerUsable.Value) return;

        using var app = new PublishedApp();

        await app.UseServerImageAsync();

        var run = await app.DeployAsync(new StaticSiteRecipe(), [("index.html", "x")], environmentPath: app.EmptyPathDirectory);

        run.ExitCode.ShouldNotBe(0);
        run.Output.ShouldContain("Docker is required on this target", Case.Sensitive, run.Describe("the failure must say docker is missing, before anything else"));
    }

    /// <summary>One published app on one free port, with everything it creates on the machine removed afterwards.</summary>
    private sealed class PublishedApp : IDisposable
    {
        private static readonly HttpClient Http = new();

        private readonly string _root = Path.Combine(Path.GetTempPath(), $"squid-e2e-publish-{Guid.NewGuid():N}");
        private int _deployments;

        public PublishedApp()
        {
            Directory.CreateDirectory(_root);

            PackageId = $"e2e-{Guid.NewGuid():N}"[..16];
            HostPort = FreePort();
        }

        public string PackageId { get; private set; }

        public int HostPort { get; set; }

        public int ProjectId { get; set; } = 7000 + Random.Shared.Next(1000);

        public int EnvironmentId { get; set; } = 1;

        public void CopyIdentityOf(PublishedApp other, int environmentId)
        {
            PackageId = other.PackageId;
            ProjectId = other.ProjectId;
            EnvironmentId = environmentId;
        }

        // The deployment's two ids make the name; the same app in another environment is another container.
        public string ContainerName => $"{ContainerAppPlan.ContainerNameBase(PackageId)}-p{ProjectId}-e{EnvironmentId}";

        public string EmptyPathDirectory
        {
            get
            {
                // A PATH with the tools the script's prelude needs and no docker.
                var directory = Path.Combine(_root, "no-docker-path");

                Directory.CreateDirectory(directory);

                foreach (var tool in new[] { "base64", "tr", "cut", "sed", "head", "wc" })
                {
                    var source = new[] { "/usr/bin", "/bin" }.Select(d => Path.Combine(d, tool)).First(File.Exists);

                    if (!File.Exists(Path.Combine(directory, tool))) File.CreateSymbolicLink(Path.Combine(directory, tool), source);
                }

                return directory;
            }
        }

        public static (string Path, string Content)[] DockerfileApp(string content, string command = "[\"httpd\",\"-f\",\"-p\",\"8080\",\"-h\",\"/www\"]", string healthPath = null)
        {
            var files = new List<(string, string)>
            {
                ("app/Dockerfile", $"FROM {BuiltImage}\nCOPY www /www\nEXPOSE 8080\nCMD {command}\n"),
                ("app/www/index.html", content)
            };

            if (healthPath != null) files.Add(("app/squid.yaml", $"health:\n  path: {healthPath}\n"));

            return files.ToArray();
        }

        public string Root => _root;

        public async Task<ScriptRun> DeployAsync(IDeployRecipe recipe, (string Path, string Content)[] files, string version = "1.0.0", int? healthTimeoutSeconds = null, string environmentPath = null, bool keepIdTokens = false, string installParent = null)
        {
            var scriptPath = await PrepareAsync(recipe, files, version, healthTimeoutSeconds, keepIdTokens, installParent);

            return Run("/bin/bash", [scriptPath], environmentPath, TimeSpan.FromMinutes(5), _root);
        }

        // Starts the script and returns while it runs, with the docker shim in front of the real docker so a test can stop it at a chosen point.
        public async Task<RunningScript> StartAsync(IDeployRecipe recipe, (string Path, string Content)[] files, DockerShim shim, string version = "1.0.0")
        {
            var scriptPath = await PrepareAsync(recipe, files, version, null, false, null);

            return RunningScript.Start(scriptPath, shim, _root);
        }

        public DockerShim NewShim(Action<DockerShim> configure)
        {
            var shim = new DockerShim(Path.Combine(_root, $"shim-{++_shims}"));

            configure(shim);

            return shim;
        }

        private int _shims;

        private async Task<string> PrepareAsync(IDeployRecipe recipe, (string Path, string Content)[] files, string version, int? healthTimeoutSeconds, bool keepIdTokens, string installParent)
        {
            var archive = Path.Combine(_root, $"package-{++_deployments}.zip");
            var installDir = Path.Combine(installParent ?? _root, $"install-{_deployments}");

            WriteZip(archive, files);
            ZipFile.ExtractToDirectory(archive, installDir);

            var facts = await InspectAsync(archive);
            var plan = await recipe.RenderAsync(new RecipeRenderContext { PackageId = PackageId, Facts = facts, TargetRoles = ["web"] }, CancellationToken.None);
            var script = UseStandInServer(Substitute(plan.Steps[1].Actions[0].Properties["Squid.Action.Script.ScriptBody"], installDir, version, healthTimeoutSeconds, keepIdTokens));
            var scriptPath = Path.Combine(_root, $"deploy-{_deployments}.sh");

            await File.WriteAllTextAsync(scriptPath, script);

            return scriptPath;
        }

        private static async Task<PackageFacts> InspectAsync(string archive)
        {
            IPackageInspector[] inspectors = [new FileMarkerInspector(), new NodeInspector(), new SquidYamlInspector(), new DockerfileInspector()];

            return (await new BuiltInArchiveVerifier(new PackageInspectionService(inspectors)).VerifyAsync(archive, BuiltInPackageFormat.Zip, "pkg", CancellationToken.None)).Facts;
        }

        // The deployment's own substitution turns the two #{...} tokens into the extraction folder and the release number before the script reaches the target.
        private string Substitute(string script, string installDir, string version, int? healthTimeoutSeconds, bool keepIdTokens)
        {
            var variables = new VariableDictionary();

            variables.Set(Squid.Message.Constants.SpecialVariables.Output.Variable(ContainerAppPlan.DeployPackageStepName, Squid.Message.Constants.SpecialVariables.Action.InstallationDirectoryPath), installDir);
            variables.Set(Squid.Message.Constants.SpecialVariables.Release.Number, version);
            variables.Set(Squid.Message.Models.Publishing.PlanInputVariables.Name(Squid.Message.Models.Publishing.PlanInputNames.HostPort), HostPort.ToString());

            if (!keepIdTokens)
            {
                variables.Set(Squid.Message.Constants.SpecialVariables.Project.Id, ProjectId.ToString());
                variables.Set(Squid.Message.Constants.SpecialVariables.Environment.Id, EnvironmentId.ToString());
            }

            var expanded = VariableExpander.ExpandString(script, variables);

            if (!keepIdTokens) expanded.ShouldNotContain("#{", Case.Sensitive, "every variable in the script must have been substituted");

            expanded.ShouldNotContain(installDir, Case.Sensitive, "the extraction folder must reach the script only as Base64, never as text");

            return healthTimeoutSeconds == null ? expanded : ShortenHealthTimeout(expanded, healthTimeoutSeconds.Value);
        }

        private string UseStandInServer(string script)
        {
            if (_standInImage == null) return script;

            var original = $"SERVER_IMAGE='{StaticSiteRecipe.ServerImage}'";

            script.ShouldContain(original, Case.Sensitive, "the script no longer names the server image this test swaps for the stand-in");

            return script.Replace(original, $"SERVER_IMAGE='{_standInImage}'", StringComparison.Ordinal);
        }

        // The wait is a named constant (60 s); a test of the "never healthy" path cannot wait that long, so it shortens that one assignment, and says so if the line moved.
        private static string ShortenHealthTimeout(string script, int seconds)
        {
            var original = $"HEALTH_TIMEOUT_SECONDS={ContainerAppPlan.HealthTimeoutSeconds}";

            script.ShouldContain(original, Case.Sensitive, "the script no longer has the health timeout assignment this test shortens");

            return script.Replace(original, $"HEALTH_TIMEOUT_SECONDS={seconds}", StringComparison.Ordinal);
        }

        private static void WriteZip(string path, IEnumerable<(string Path, string Content)> files)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

            foreach (var (entryPath, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entryPath).Open(), new UTF8Encoding(false));

                writer.Write(content);
            }
        }

        // The real server image when it is here or can be pulled quickly; otherwise a stand-in with the same command line (see the class comment).
        public async Task UseServerImageAsync()
        {
            if (Run("docker", ["image", "inspect", StaticSiteRecipe.ServerImage], null, TimeSpan.FromSeconds(30)).ExitCode == 0) return;

            if (TryRun("docker", ["pull", "--quiet", StaticSiteRecipe.ServerImage], TimeSpan.FromSeconds(25))) return;

            _standInImage = $"squid-e2e-caddy-standin-{Guid.NewGuid():N}"[..40];

            var context = Path.Combine(_root, "standin");

            Directory.CreateDirectory(context);
            await File.WriteAllTextAsync(Path.Combine(context, "caddy"), StandInCaddy);
            await File.WriteAllTextAsync(Path.Combine(context, "Dockerfile"), $"FROM {BuiltImage}\nCOPY caddy /usr/local/bin/caddy\nRUN chmod +x /usr/local/bin/caddy\n");

            var built = Run("docker", ["build", "-q", "-t", _standInImage, context], null, TimeSpan.FromMinutes(2));

            built.ExitCode.ShouldBe(0, built.Describe("the stand-in server image could not be built"));
        }

        private const string StandInCaddy = "#!/bin/sh\nroot=/srv; listen=:80\nwhile [ $# -gt 0 ]; do case \"$1\" in --root) root=\"$2\"; shift;; --listen) listen=\"$2\"; shift;; esac; shift; done\nexec httpd -f -p \"${listen#:}\" -h \"$root\"\n";

        private string _standInImage;

        private static bool TryRun(string file, IEnumerable<string> arguments, TimeSpan timeout)
        {
            try
            {
                return Run(file, arguments, null, timeout).ExitCode == 0;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public Task<string> GetAsync(string path) => GetAsync(HostPort, path);

        public static async Task<string> GetAsync(int port, string path)
        {
            using var response = await Http.GetAsync($"http://127.0.0.1:{port}{path}");

            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"GET {path} on port {port}");

            return await response.Content.ReadAsStringAsync();
        }

        public bool IsRunning(string name) => Run("docker", ["inspect", "--format", "{{.State.Running}}", name], null, TimeSpan.FromSeconds(30)).Output.Trim() == "true";

        public string[] ContainersOf()
        {
            var listed = Run("docker", ["ps", "-a", "--format", "{{.Names}}", "--filter", $"name={ContainerName}"], null, TimeSpan.FromSeconds(30));

            return listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal).ToArray();
        }

        public string[] ImageTags()
        {
            var listed = Run("docker", ["images", ContainerName, "--format", "{{.Tag}}"], null, TimeSpan.FromSeconds(30));

            return listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        public void Dispose()
        {
            foreach (var name in new[] { ContainerName, ContainerName + "-candidate", ContainerName + "-previous" })
                Run("docker", ["rm", "-f", name], null, TimeSpan.FromSeconds(60));

            foreach (var image in Run("docker", ["images", ContainerName, "-q"], null, TimeSpan.FromSeconds(30)).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                Run("docker", ["rmi", "-f", image], null, TimeSpan.FromSeconds(60));

            if (_standInImage != null) Run("docker", ["rmi", "-f", _standInImage], null, TimeSpan.FromSeconds(60));

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // best effort: a temp folder left behind is harmless
            }
        }
    }

    public sealed record ScriptRun(int ExitCode, string Output)
    {
        public string Describe(string message) => $"{message}\n--- exit code {ExitCode}, output ---\n{Output}";
    }

    private static ScriptRun Run(string file, IEnumerable<string> arguments, string pathOverride, TimeSpan timeout, string workingDirectory = null)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };

        if (workingDirectory != null) info.WorkingDirectory = workingDirectory;

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        if (pathOverride != null) info.Environment["PATH"] = pathOverride;

        using var process = Process.Start(info)!;
        var output = new StringBuilder();

        process.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{file} {string.Join(' ', arguments)}' did not finish within {timeout}. Output so far:\n{output}");
        }

        process.WaitForExit();

        lock (output) return new ScriptRun(process.ExitCode, output.ToString());
    }

    /// <summary>
    /// A stand-in for the <c>docker</c> command that runs the real one and then, on request, stops the container it just started and/or pauses itself so the test can signal the
    /// script at an exact point of the swap. It is a test tool only: the production script has no hook for it.
    /// </summary>
    private sealed class DockerShim
    {
        private readonly string _directory;

        public DockerShim(string directory)
        {
            _directory = directory;

            Directory.CreateDirectory(Path.Combine(directory, "bin"));
            Directory.CreateDirectory(Path.Combine(directory, "state"));

            var path = Path.Combine(directory, "bin", "docker");

            File.WriteAllText(path, Script);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        /// <summary>Pause (sleep) after the Nth call of this docker subcommand has finished.</summary>
        public string PauseAfter { get; set; }

        public int PauseNth { get; set; } = 1;

        /// <summary>Stop the container the Nth <c>docker run</c> started, right after it started.</summary>
        public int? StopAfterRun { get; set; }

        public string PausedMarker => Path.Combine(_directory, "state", "paused");

        public IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>
        {
            ["PATH"] = Path.Combine(_directory, "bin") + ":" + System.Environment.GetEnvironmentVariable("PATH"),
            ["SHIM_REAL_DOCKER"] = RealDocker(),
            ["SHIM_STATE"] = Path.Combine(_directory, "state"),
            ["SHIM_PAUSE_AFTER"] = PauseAfter ?? string.Empty,
            ["SHIM_PAUSE_NTH"] = PauseNth.ToString(),
            ["SHIM_STOP_AFTER_RUN"] = StopAfterRun?.ToString() ?? string.Empty
        };

        private static string RealDocker() => Run("/bin/bash", ["-c", "command -v docker"], null, TimeSpan.FromSeconds(10)).Output.Trim();

        private const string Script = "#!/bin/bash\nreal=\"$SHIM_REAL_DOCKER\"\nsub=\"$1\"\nstate=\"$SHIM_STATE\"\n\"$real\" \"$@\"\ncode=$?\ncount=\"$state/$sub.count\"\nn=$(( $(cat \"$count\" 2>/dev/null || echo 0) + 1 ))\necho \"$n\" > \"$count\"\nif [ \"$sub\" = run ] && [ -n \"$SHIM_STOP_AFTER_RUN\" ] && [ \"$n\" = \"$SHIM_STOP_AFTER_RUN\" ]; then\n  name=\"\"; prev=\"\"\n  for a in \"$@\"; do if [ \"$prev\" = --name ]; then name=\"$a\"; fi; prev=\"$a\"; done\n  \"$real\" stop \"$name\" >/dev/null 2>&1\nfi\nif [ -n \"$SHIM_PAUSE_AFTER\" ] && [ \"$sub\" = \"$SHIM_PAUSE_AFTER\" ] && [ \"$n\" = \"$SHIM_PAUSE_NTH\" ]; then\n  : > \"$state/paused\"\n  exec sleep 300\nfi\nexit $code\n";
    }

    /// <summary>A script started in the background: it can be waited for, or signalled while it sits at the shim's pause.</summary>
    private sealed class RunningScript
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();
        private readonly DockerShim _shim;

        private RunningScript(Process process, DockerShim shim)
        {
            _process = process;
            _shim = shim;
        }

        public string Output
        {
            get
            {
                lock (_output) return _output.ToString();
            }
        }

        public static RunningScript Start(string scriptPath, DockerShim shim, string workingDirectory)
        {
            var info = new ProcessStartInfo("/bin/bash") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };

            info.ArgumentList.Add(scriptPath);

            foreach (var (key, value) in shim.Environment) info.Environment[key] = value;

            var process = Process.Start(info)!;
            var running = new RunningScript(process, shim);

            process.OutputDataReceived += (_, e) => { lock (running._output) running._output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { lock (running._output) running._output.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return running;
        }

        public async Task<ScriptRun> WaitForExitAsync()
        {
            if (!await Task.Run(() => _process.WaitForExit(300_000))) throw new TimeoutException($"The script did not end. Output:\n{Output}");

            _process.WaitForExit();

            return new ScriptRun(_process.ExitCode, Output);
        }

        public async Task WaitUntilPausedAsync()
        {
            var deadline = DateTime.UtcNow.AddMinutes(3);

            while (!File.Exists(_shim.PausedMarker))
            {
                if (_process.HasExited) throw new InvalidOperationException($"The script ended before reaching the pause point. Output:\n{Output}");
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"The script did not reach the pause point (docker {_shim.PauseAfter} #{_shim.PauseNth}). Output:\n{Output}");

                await Task.Delay(100);
            }
        }

        /// <summary>SIGTERM to the script and to the paused command under it, as a stopping agent would; the script's trap then runs.</summary>
        public ScriptRun Terminate() => Signal("TERM", children: true);

        /// <summary>SIGKILL to the script and the paused command: nothing can trap it, so only the next run can put things right.</summary>
        public ScriptRun Kill() => Signal("KILL", children: true);

        private ScriptRun Signal(string signal, bool children)
        {
            var pid = _process.Id.ToString();

            if (signal == "TERM") Run("/bin/kill", ["-TERM", pid], null, TimeSpan.FromSeconds(10));
            if (children) Run("/usr/bin/pkill", [$"-{signal}", "-P", pid], null, TimeSpan.FromSeconds(10));
            if (signal == "KILL") Run("/bin/kill", ["-KILL", pid], null, TimeSpan.FromSeconds(10));

            if (!_process.WaitForExit(120_000)) throw new TimeoutException($"The script did not end after SIG{signal}. Output:\n{Output}");

            _process.WaitForExit();

            return new ScriptRun(_process.ExitCode, Output);
        }
    }

    // Rule 12.8: a port the OS hands out, never a hard-coded one.
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

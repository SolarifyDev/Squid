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

        public string PackageId { get; }

        public int HostPort { get; set; }

        public string ContainerName => ContainerAppPlan.ContainerName(PackageId);

        public string EmptyPathDirectory
        {
            get
            {
                var directory = Path.Combine(_root, "empty-path");

                Directory.CreateDirectory(directory);

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

        public async Task<ScriptRun> DeployAsync(IDeployRecipe recipe, (string Path, string Content)[] files, string version = "1.0.0", int? healthTimeoutSeconds = null, string environmentPath = null)
        {
            var archive = Path.Combine(_root, $"package-{++_deployments}.zip");
            var installDir = Path.Combine(_root, $"install-{_deployments}");

            WriteZip(archive, files);
            ZipFile.ExtractToDirectory(archive, installDir);

            var facts = await InspectAsync(archive);
            var plan = await recipe.RenderAsync(new RecipeRenderContext { PackageId = PackageId, Facts = facts, HostPort = HostPort, TargetRoles = ["web"] }, CancellationToken.None);
            var script = UseStandInServer(Substitute(plan.Steps[1].Actions[0].Properties["Squid.Action.Script.ScriptBody"], installDir, version, healthTimeoutSeconds));
            var scriptPath = Path.Combine(_root, $"deploy-{_deployments}.sh");

            await File.WriteAllTextAsync(scriptPath, script);

            return Run("/bin/bash", [scriptPath], environmentPath, TimeSpan.FromMinutes(5));
        }

        private static async Task<PackageFacts> InspectAsync(string archive)
        {
            IPackageInspector[] inspectors = [new FileMarkerInspector(), new NodeInspector(), new SquidYamlInspector(), new DockerfileInspector()];

            return await new PackageInspectionService(inspectors).InspectAsync(archive, BuiltInPackageFormat.Zip, CancellationToken.None);
        }

        // The deployment's own substitution turns the two #{...} tokens into the extraction folder and the release number before the script reaches the target.
        private static string Substitute(string script, string installDir, string version, int? healthTimeoutSeconds)
        {
            var variables = new VariableDictionary();

            variables.Set(Squid.Message.Constants.SpecialVariables.Output.Variable(ContainerAppPlan.DeployPackageStepName, Squid.Message.Constants.SpecialVariables.Action.InstallationDirectoryPath), installDir);
            variables.Set(Squid.Message.Constants.SpecialVariables.Release.Number, version);

            var expanded = VariableExpander.ExpandString(script, variables);

            expanded.ShouldNotContain("#{", Case.Sensitive, "every variable in the script must have been substituted");

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

    private static ScriptRun Run(string file, IEnumerable<string> arguments, string pathOverride, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };

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

using Squid.Calamari.Tests.TestSupport;
using System.IO.Compression;

namespace Squid.Calamari.Tests.Calamari.Cli;

[Collection("Process Environment")]
public class CliSmokeTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NoArgs_Returns1_AndPrintsUsage()
    {
        var result = await CalamariTestHost.InvokeCliAsync();

        result.ExitCode.ShouldBe(1);
        result.Stdout.ShouldContain("squid-calamari <subcommand> [options]");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunScript_HappyPath_Returns0_AndPrintsScriptOutput()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-cli-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var scriptPath = Path.Combine(tempDir, "smoke.sh");
            File.WriteAllText(scriptPath,
                "echo hello-from-cli\n" +
                "echo \"##squid[setVariable name='X' value='1']\"\n");

            var result = await CalamariTestHost.InvokeCliAsync(tempDir, "run-script", $"--script={scriptPath}");

            result.ExitCode.ShouldBe(0);
            result.Stdout.ShouldContain("hello-from-cli");
            result.Stdout.ShouldNotContain("##squid[setVariable");
            result.Stderr.ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task OctopusCompat_KubernetesApplyRawYaml_HappyPath_Returns0()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-cli-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            var yamlPath = Path.Combine(tempDir, "deployment.yaml");
            File.WriteAllText(yamlPath, "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: test\n");

            var varsPath = Path.Combine(tempDir, "variables.json");
            File.WriteAllText(varsPath, "{}");

            var kubectlPath = Path.Combine(tempDir, "kubectl");
            File.WriteAllText(
                kubectlPath,
                "#!/usr/bin/env bash\n" +
                "echo \"compat kubectl $*\"\n" +
                "echo \"##squid[setVariable name='AppliedCompat' value='True']\"\n");

            File.SetUnixFileMode(
                kubectlPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            Environment.SetEnvironmentVariable("PATH", tempDir + Path.PathSeparator + originalPath);

            var result = await CalamariTestHost.InvokeCliAsync(
                tempDir,
                "kubernetes-apply-raw-yaml",
                $"--package={yamlPath}",
                $"--variables={varsPath}",
                "--namespace=test-ns");

            result.ExitCode.ShouldBe(0);
            result.Stdout.ShouldContain("compat kubectl apply -f");
            result.Stdout.ShouldContain("--namespace test-ns");
            result.Stdout.ShouldNotContain("##squid[setVariable");
            result.Stderr.ShouldBeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);

            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task OctopusCompat_KubernetesApplyRawYaml_ZipPackage_HappyPath_Returns0()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-cli-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            var packagePath = Path.Combine(tempDir, "manifests.zip");
            using (var stream = File.Create(packagePath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("k8s/deployment.yaml");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: test\n");
            }

            var varsPath = Path.Combine(tempDir, "variables.json");
            File.WriteAllText(varsPath, "{}");

            var kubectlPath = Path.Combine(tempDir, "kubectl");
            File.WriteAllText(
                kubectlPath,
                "#!/usr/bin/env bash\n" +
                "echo \"compat-zip kubectl $*\"\n");

            File.SetUnixFileMode(
                kubectlPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            Environment.SetEnvironmentVariable("PATH", tempDir + Path.PathSeparator + originalPath);

            var result = await CalamariTestHost.InvokeCliAsync(
                tempDir,
                "kubernetes-apply-raw-yaml",
                $"--package={packagePath}",
                $"--variables={varsPath}",
                "--namespace=test-ns");

            result.ExitCode.ShouldBe(0);
            result.Stdout.ShouldContain("compat-zip kubectl apply -f");
            result.Stderr.ShouldBeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);

            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Squid.UnitTests.Setup;

/// <summary>
/// The all-in-one image's short start parameters (SQUID_DB, ...) are its public interface. Runs the
/// real entrypoint script and checks what it hands to the server.
/// </summary>
public sealed class EntrypointParameterMappingTests
{
    private static string ScriptPath => Path.Combine(RepositoryRoot(), "docker", "squid", "entrypoint.sh");

    [Theory]
    [InlineData("SQUID_DB", "SquidStore__ConnectionString")]
    [InlineData("SQUID_REDIS", "RedisCacheConnectionString")]
    [InlineData("SQUID_ADMIN_USER", "Bootstrap__Admin__UserName")]
    [InlineData("SQUID_ADMIN_PASSWORD", "Bootstrap__Admin__Password")]
    [InlineData("SQUID_URL", "ServerUrl__ExternalUrl")]
    [InlineData("SQUID_POLLING_URL", "ServerUrl__CommsUrl")]
    public void ShortName_MapsToConfigurationKey(string shortName, string longName)
    {
        if (OperatingSystem.IsWindows()) return;

        var environment = RunEntrypoint(new() { [shortName] = "Host=a;Password=x=y" });

        environment[longName].ShouldBe("Host=a;Password=x=y");
    }

    [Fact]
    public void ExplicitLongName_Wins()
    {
        if (OperatingSystem.IsWindows()) return;

        var environment = RunEntrypoint(new() { ["SQUID_DB"] = "short", ["SquidStore__ConnectionString"] = "long" });

        environment["SquidStore__ConnectionString"].ShouldBe("long");
    }

    [Fact]
    public void NothingGiven_SetsNothing()
    {
        if (OperatingSystem.IsWindows()) return;

        RunEntrypoint(new()).Keys.ShouldNotContain("SquidStore__ConnectionString");
    }

    private static Dictionary<string, string> RunEntrypoint(Dictionary<string, string> environment)
    {
        var info = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add(ScriptPath);
        info.ArgumentList.Add("env");

        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("SQUID_") || k.Contains("__")).ToList()) info.Environment.Remove(key);
        foreach (var (key, value) in environment) info.Environment[key] = value;

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        process.ExitCode.ShouldBe(0);

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.Last()[1]);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Squid.sln"))) directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Squid.sln not found above the test output directory");
    }
}

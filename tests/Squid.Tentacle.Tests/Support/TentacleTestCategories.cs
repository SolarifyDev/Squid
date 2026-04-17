namespace Squid.Tentacle.Tests.Support;

public static class TentacleTestCategories
{
    public const string Core = "Tentacle.Core";
    public const string Flavor = "Tentacle.Flavor";
    public const string Lifecycle = "Tentacle.Lifecycle";
    public const string Integration = "Tentacle.Integration";
    public const string Kubernetes = "Tentacle.Kubernetes";

    /// <summary>
    /// Tests that require a Windows host with pwsh and/or powershell installed.
    /// Gated off the default test runs (Linux CI / local dev) and run nightly +
    /// on merge to main via the tentacle-windows-e2e.yml GitHub Actions workflow.
    /// Tests in this category must also gracefully skip if run on a non-Windows
    /// host so that `dotnet test` locally on macOS/Linux doesn't fail them.
    /// </summary>
    public const string WindowsTentacleE2E = "WindowsTentacleE2E";
}

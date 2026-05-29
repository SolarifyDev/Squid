using Squid.Core.Services.Machines.Cleanup;
using Squid.Message.Hardening;

namespace Squid.UnitTests.Services.Machines.Cleanup;

[CollectionDefinition("MachineCleanupEnforcementEnv", DisableParallelization = true)]
public class MachineCleanupEnforcementEnvCollection { }

/// <summary>
/// Pins the operator escape hatch (Rule 8) + the three-mode resolution (Rule 11)
/// for machine cleanup. Default is <c>Warn</c> (dry-run) so the destructive delete
/// is opt-in. Shares a serial collection because the env var is process-global.
/// </summary>
[Collection("MachineCleanupEnforcementEnv")]
public class MachineCleanupEnforcementTests
{
    [Fact]
    public void EnvVar_ConstantNamePinned()
    {
        // Renaming this breaks every operator who pinned the cleanup mode via env.
        MachineCleanupEnforcement.EnvVar.ShouldBe("SQUID_MACHINE_CLEANUP_ENFORCEMENT");
    }

    [Fact]
    public void Unset_DefaultsToWarn()
        => WithEnv(null, () => MachineCleanupEnforcement.ResolveMode().ShouldBe(EnforcementMode.Warn));

    [Theory]
    [InlineData("off", EnforcementMode.Off)]
    [InlineData("warn", EnforcementMode.Warn)]
    [InlineData("strict", EnforcementMode.Strict)]
    [InlineData("STRICT", EnforcementMode.Strict)]
    [InlineData("garbage", EnforcementMode.Warn)]
    public void ResolvesMode(string raw, EnforcementMode expected)
        => WithEnv(raw, () => MachineCleanupEnforcement.ResolveMode().ShouldBe(expected));

    private static void WithEnv(string value, Action body)
    {
        var original = Environment.GetEnvironmentVariable(MachineCleanupEnforcement.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(MachineCleanupEnforcement.EnvVar, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(MachineCleanupEnforcement.EnvVar, original);
        }
    }
}

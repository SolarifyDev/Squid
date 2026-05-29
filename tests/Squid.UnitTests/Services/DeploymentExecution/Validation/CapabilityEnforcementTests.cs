using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Message.Hardening;

namespace Squid.UnitTests.Services.DeploymentExecution.Validation;

/// <summary>
/// Serialises every test that mutates the process-wide
/// <c>SQUID_CAPABILITY_ENFORCEMENT</c> env var, so parallel collections can't
/// observe each other's overrides.
/// </summary>
[CollectionDefinition("CapabilityEnforcementEnv", DisableParallelization = true)]
public class CapabilityEnforcementEnvCollection { }

/// <summary>
/// Rule 11 pinning for <see cref="CapabilityEnforcement"/>: the env-var name
/// (Rule 8) and the off/warn/strict parse + the non-breaking default (warn).
/// </summary>
[Collection("CapabilityEnforcementEnv")]
public class CapabilityEnforcementTests
{
    [Fact]
    public void EnvVar_ConstantNamePinned()
        // Renaming this breaks every operator who pinned strict/off via env.
        => CapabilityEnforcement.EnvVar.ShouldBe("SQUID_CAPABILITY_ENFORCEMENT");

    [Fact]
    public void ResolveMode_Unset_DefaultsToWarn()
        => WithEnv(null, () => CapabilityEnforcement.ResolveMode().ShouldBe(EnforcementMode.Warn,
            customMessage: "Default MUST be Warn so the feature is non-breaking on upgrade (skip + warn, as today)."));

    [Theory]
    [InlineData("off", EnforcementMode.Off)]
    [InlineData("warn", EnforcementMode.Warn)]
    [InlineData("strict", EnforcementMode.Strict)]
    [InlineData("STRICT", EnforcementMode.Strict)]
    [InlineData("garbage", EnforcementMode.Warn)]
    public void ResolveMode_ParsesEnvVar(string value, EnforcementMode expected)
        => WithEnv(value, () => CapabilityEnforcement.ResolveMode().ShouldBe(expected));

    private static void WithEnv(string value, System.Action body)
    {
        var original = System.Environment.GetEnvironmentVariable(CapabilityEnforcement.EnvVar);
        try
        {
            System.Environment.SetEnvironmentVariable(CapabilityEnforcement.EnvVar, value);
            body();
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(CapabilityEnforcement.EnvVar, original);
        }
    }
}

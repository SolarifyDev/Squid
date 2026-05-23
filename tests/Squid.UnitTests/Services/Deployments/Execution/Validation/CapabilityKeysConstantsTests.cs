using Shouldly;
using Squid.Core.Services.DeploymentExecution.Validation;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Execution.Validation;

/// <summary>
/// Hard-pins every literal in <see cref="CapabilityKeys"/>. Handler authors
/// reference these as <c>CapabilityKeys.Os.Windows</c> etc.; the projection
/// helper <c>MachineCapabilitySet</c> emits the same literals into the slot
/// map. A silent rename would create asymmetric drift between the two halves
/// (handler declares "windows", target advertises "Windows" → no match).
///
/// <para>Per Rule 8: every literal that crosses a wire boundary or appears
/// in operator-facing UI gets a build-time pin.</para>
/// </summary>
public class CapabilityKeysConstantsTests
{
    [Fact]
    public void Present_Sentinel()
        => CapabilityKeys.Present.ShouldBe("present");

    [Fact]
    public void OsSlot_LiteralPinned()
        => CapabilityKeys.OsSlot.ShouldBe("os");

    [Fact]
    public void ArchSlot_LiteralPinned()
        => CapabilityKeys.ArchSlot.ShouldBe("arch");

    [Fact]
    public void Os_Windows_LiteralPinned()
        => CapabilityKeys.Os.Windows.ShouldBe("windows");

    [Fact]
    public void Os_Linux_LiteralPinned()
        => CapabilityKeys.Os.Linux.ShouldBe("linux");

    [Fact]
    public void Os_MacOS_LiteralPinned()
        => CapabilityKeys.Os.MacOS.ShouldBe("macos");

    [Fact]
    public void Arch_X64_LiteralPinned()
        => CapabilityKeys.Arch.X64.ShouldBe("x64");

    [Fact]
    public void Arch_Arm64_LiteralPinned()
        => CapabilityKeys.Arch.Arm64.ShouldBe("arm64");

    [Fact]
    public void Arch_X86_LiteralPinned()
        => CapabilityKeys.Arch.X86.ShouldBe("x86");

    [Theory]
    [InlineData("shell:powershell", "PowerShell")]
    [InlineData("shell:pwsh", "Pwsh")]
    [InlineData("shell:bash", "Bash")]
    [InlineData("shell:cmd", "Cmd")]
    [InlineData("shell:sh", "Sh")]
    public void Shell_SlotNamespacePinned(string expectedKey, string name)
    {
        var actual = typeof(CapabilityKeys.Shell)
            .GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?.GetRawConstantValue() as string;

        actual.ShouldBe(expectedKey,
            customMessage:
                $"CapabilityKeys.Shell.{name} must equal '{expectedKey}'. " +
                "The 'shell:' namespace prefix is the projection contract between handler requirements " +
                "and MachineCapabilitySet — drift here silently breaks shell-based plan-time gating.");
    }

    [Theory]
    [InlineData("bin:kubectl", "Kubectl")]
    [InlineData("bin:helm", "Helm")]
    [InlineData("bin:docker", "Docker")]
    [InlineData("bin:kustomize", "Kustomize")]
    [InlineData("bin:az", "Az")]
    [InlineData("bin:aws", "Aws")]
    [InlineData("bin:gcloud", "Gcloud")]
    [InlineData("bin:terraform", "Terraform")]
    public void Bin_SlotNamespacePinned(string expectedKey, string name)
    {
        var actual = typeof(CapabilityKeys.Bin)
            .GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?.GetRawConstantValue() as string;

        actual.ShouldBe(expectedKey);
    }

    [Theory]
    [InlineData("priv:admin", "Admin")]
    [InlineData("priv:sudo", "Sudo")]
    public void Privilege_SlotNamespacePinned(string expectedKey, string name)
    {
        var actual = typeof(CapabilityKeys.Privilege)
            .GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?.GetRawConstantValue() as string;

        actual.ShouldBe(expectedKey);
    }

    [Theory]
    [InlineData("role:iis", "IIS")]
    [InlineData("role:docker", "Docker")]
    [InlineData("role:nginx", "Nginx")]
    [InlineData("role:systemd", "Systemd")]
    public void Role_SlotNamespacePinned(string expectedKey, string name)
    {
        // H7 — audit followup. Shell / Bin / Privilege namespaces had pinning
        // tests; the Role namespace shipped in 1.8.0 without one. A rename like
        // CapabilityKeys.Role.IIS → .Iis would have been a silent contract
        // break — IISDeployActionHandler.StaticRequirements references the
        // constant symbol, but a downgrade of its value (e.g. "role:iis" →
        // "role:IIS") would silently make the validator's case-insensitive
        // overlap check stop matching the agent's lowercase "iis" advertisement.
        var actual = typeof(CapabilityKeys.Role)
            .GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?.GetRawConstantValue() as string;

        actual.ShouldBe(expectedKey);
    }

    [Fact]
    public void CategoricalSlots_ContainsOsAndArch_Only()
    {
        CapabilityKeys.CategoricalSlots.Count.ShouldBe(2);
        CapabilityKeys.CategoricalSlots.ShouldContain(CapabilityKeys.OsSlot);
        CapabilityKeys.CategoricalSlots.ShouldContain(CapabilityKeys.ArchSlot);
    }
}

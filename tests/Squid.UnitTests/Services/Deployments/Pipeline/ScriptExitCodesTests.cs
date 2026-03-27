using System.Linq;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class ScriptExitCodesTests
{
    [Theory]
    [InlineData(ScriptExitCodes.Success, 0)]
    [InlineData(ScriptExitCodes.UnknownResult, -1)]
    [InlineData(ScriptExitCodes.Fatal, -41)]
    [InlineData(ScriptExitCodes.PowerShellInvalid, -42)]
    [InlineData(ScriptExitCodes.Canceled, -43)]
    [InlineData(ScriptExitCodes.Timeout, -44)]
    [InlineData(ScriptExitCodes.ProcessTerminated, -45)]
    [InlineData(ScriptExitCodes.PodNotFound, -81)]
    [InlineData(ScriptExitCodes.ContainerTerminated, -82)]
    public void ExitCode_HasExpectedValue(int actual, int expected)
    {
        actual.ShouldBe(expected);
    }

    [Theory]
    [InlineData(ScriptExitCodes.Fatal, true)]
    [InlineData(ScriptExitCodes.Timeout, true)]
    [InlineData(ScriptExitCodes.PodNotFound, true)]
    [InlineData(ScriptExitCodes.ContainerTerminated, true)]
    [InlineData(ScriptExitCodes.ProcessTerminated, true)]
    [InlineData(ScriptExitCodes.Success, false)]
    [InlineData(ScriptExitCodes.Canceled, false)]
    [InlineData(ScriptExitCodes.UnknownResult, false)]
    [InlineData(ScriptExitCodes.PowerShellInvalid, false)]
    [InlineData(1, false)]
    [InlineData(127, false)]
    public void IsInfrastructureFailure_ClassifiesCorrectly(int exitCode, bool expected)
    {
        ScriptExitCodes.IsInfrastructureFailure(exitCode).ShouldBe(expected);
    }

    [Theory]
    [InlineData(ScriptExitCodes.Success, "Success")]
    [InlineData(ScriptExitCodes.Canceled, "Script execution canceled")]
    [InlineData(ScriptExitCodes.Timeout, "Script execution timed out")]
    [InlineData(ScriptExitCodes.PodNotFound, "Kubernetes pod not found")]
    [InlineData(ScriptExitCodes.Fatal, "Fatal infrastructure failure")]
    [InlineData(ScriptExitCodes.ContainerTerminated, "Kubernetes container terminated unexpectedly")]
    [InlineData(ScriptExitCodes.ProcessTerminated, "Process terminated unexpectedly")]
    [InlineData(ScriptExitCodes.PowerShellInvalid, "Invalid PowerShell script")]
    [InlineData(ScriptExitCodes.UnknownResult, "Unknown result (ticket or process not found)")]
    public void Describe_ReturnsExpectedDescription(int exitCode, string expected)
    {
        ScriptExitCodes.Describe(exitCode).ShouldBe(expected);
    }

    [Theory]
    [InlineData(1, "General error")]
    [InlineData(2, "Misuse of shell builtin or invalid argument")]
    [InlineData(126, "Command found but not executable (permission denied)")]
    [InlineData(127, "Command not found — check that the required binary (helm, kubectl, etc.) is installed and in PATH")]
    [InlineData(137, "Process killed by signal 9 (SIGKILL)")]
    [InlineData(143, "Process killed by signal 15 (SIGTERM)")]
    public void Describe_WellKnownUnixCodes_ReturnsDescriptiveMessage(int exitCode, string expected)
    {
        ScriptExitCodes.Describe(exitCode).ShouldBe(expected);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(255)]
    public void Describe_UnknownCode_ReturnsGenericDescription(int exitCode)
    {
        ScriptExitCodes.Describe(exitCode).ShouldBe($"Script exited with code {exitCode}");
    }

    [Fact]
    public void AllWellKnownCodes_AreNegative_ExceptSuccess()
    {
        ScriptExitCodes.Success.ShouldBe(0);
        ScriptExitCodes.UnknownResult.ShouldBeLessThan(0);
        ScriptExitCodes.Fatal.ShouldBeLessThan(0);
        ScriptExitCodes.PowerShellInvalid.ShouldBeLessThan(0);
        ScriptExitCodes.Canceled.ShouldBeLessThan(0);
        ScriptExitCodes.Timeout.ShouldBeLessThan(0);
        ScriptExitCodes.ProcessTerminated.ShouldBeLessThan(0);
        ScriptExitCodes.PodNotFound.ShouldBeLessThan(0);
        ScriptExitCodes.ContainerTerminated.ShouldBeLessThan(0);
    }

    [Fact]
    public void AllWellKnownCodes_AreUnique()
    {
        var codes = new[]
        {
            ScriptExitCodes.Success,
            ScriptExitCodes.UnknownResult,
            ScriptExitCodes.Fatal,
            ScriptExitCodes.PowerShellInvalid,
            ScriptExitCodes.Canceled,
            ScriptExitCodes.Timeout,
            ScriptExitCodes.ProcessTerminated,
            ScriptExitCodes.PodNotFound,
            ScriptExitCodes.ContainerTerminated
        };

        codes.Distinct().Count().ShouldBe(codes.Length);
    }
}

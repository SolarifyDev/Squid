using Shouldly;
using Squid.Message.Models.Machines;
using Xunit;

namespace Squid.UnitTests.Services.Machines;

/// <summary>
/// H3 — wire-stability pin (Rule 8) for <see cref="ManualHealthCheckErrorCodes"/>.
/// These literal strings are serialised in the upgrade-info API response and
/// consumed by the FE / CLI / external SDKs to drive error-UI branching. A
/// rename here breaks every consumer that parses the response. Pin the
/// literals so a refactor becomes a test-time-visible decision.
///
/// <para><b>Convention</b>: add new codes as new constants; NEVER rename
/// existing ones. If a code becomes obsolete, mark <c>[Obsolete]</c> but
/// don't delete or rename.</para>
/// </summary>
public sealed class ManualHealthCheckErrorCodesIntegrityTests
{
    [Fact]
    public void MachineNotFound_LiteralPinned()
        => ManualHealthCheckErrorCodes.MachineNotFound.ShouldBe("machine_not_found");

    [Fact]
    public void MachineDisabled_LiteralPinned()
        => ManualHealthCheckErrorCodes.MachineDisabled.ShouldBe("machine_disabled");

    [Fact]
    public void NoHealthChecker_LiteralPinned()
        => ManualHealthCheckErrorCodes.NoHealthChecker.ShouldBe("no_health_checker");

    [Fact]
    public void AgentUnreachable_LiteralPinned()
        => ManualHealthCheckErrorCodes.AgentUnreachable.ShouldBe("agent_unreachable");

    [Fact]
    public void AllCodes_LowerSnakeCase_StableConvention()
    {
        // Convention: lower_snake_case so consumers can split-on-underscore +
        // proper-case for display, or use the literal as a JSON enum key.
        // Future codes MUST follow the same convention — pin it explicitly.
        var codes = new[]
        {
            ManualHealthCheckErrorCodes.MachineNotFound,
            ManualHealthCheckErrorCodes.MachineDisabled,
            ManualHealthCheckErrorCodes.NoHealthChecker,
            ManualHealthCheckErrorCodes.AgentUnreachable
        };

        foreach (var code in codes)
        {
            code.ShouldMatch(@"^[a-z]+(_[a-z]+)*$",
                customMessage: $"Error code '{code}' violates the lower_snake_case convention. " +
                               "Future codes MUST be lower_snake_case so consumers can parse them deterministically.");
        }
    }
}

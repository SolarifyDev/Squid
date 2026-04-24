using System.Reflection;
using Squid.Message.Commands.Authorization;
using Squid.Message.Commands.Teams;
using Squid.Message.Contracts;

namespace Squid.UnitTests.Services.Authorization;

/// <summary>
/// P0-D.2 regression guard (2026-04-24 audit). Pre-fix, 10 team / role commands did
/// NOT implement <see cref="ISpaceScoped"/>, so <c>SpaceIdInjectionSpecification</c>
/// silently skipped them. A <c>TeamEdit</c> holder could submit
/// <c>AssignRoleToTeamCommand</c> with a body-supplied <c>SpaceId</c>, and the
/// permission check would run against that body value — not the header-injected
/// one — letting the caller drift to any Space they wanted.
///
/// <para>These tests pin every one of the 10 commands to the <see cref="ISpaceScoped"/>
/// contract AND to the reflection contract the middleware depends on (writable
/// <c>int? SpaceId</c> property). Removing either requirement would silently re-open
/// the body-drift vector without any other test catching it.</para>
/// </summary>
public sealed class TeamRoleCommandsSpaceScopedTests
{
    public static IEnumerable<object[]> SpaceScopedCommandTypes()
    {
        yield return new object[] { typeof(AssignRoleToTeamCommand) };
        yield return new object[] { typeof(UpdateRoleScopeCommand) };
        yield return new object[] { typeof(AddTeamMemberCommand) };
        yield return new object[] { typeof(RemoveTeamMemberCommand) };
        yield return new object[] { typeof(UpdateTeamCommand) };
        yield return new object[] { typeof(DeleteTeamCommand) };
        yield return new object[] { typeof(CreateUserRoleCommand) };
        yield return new object[] { typeof(UpdateUserRoleCommand) };
        yield return new object[] { typeof(DeleteUserRoleCommand) };
        yield return new object[] { typeof(RemoveRoleFromTeamCommand) };
    }

    [Theory]
    [MemberData(nameof(SpaceScopedCommandTypes))]
    public void Command_ImplementsISpaceScoped(Type commandType)
    {
        typeof(ISpaceScoped).IsAssignableFrom(commandType).ShouldBeTrue(
            customMessage:
                $"{commandType.Name} must implement ISpaceScoped so SpaceIdInjectionSpecification populates " +
                "SpaceId from the X-Space-Id header. Without this, a body-supplied SpaceId is accepted " +
                "verbatim — any caller can drift their authorization scope to another Space.");
    }

    [Theory]
    [MemberData(nameof(SpaceScopedCommandTypes))]
    public void Command_ExposesWritableNullableSpaceIdProperty(Type commandType)
    {
        // The middleware (SpaceIdInjectionSpecification) finds the property by reflection
        // using exactly these predicates. A drift to int, long?, or a read-only property
        // causes the middleware to silently skip the command.
        var prop = commandType.GetProperty("SpaceId", BindingFlags.Public | BindingFlags.Instance);

        prop.ShouldNotBeNull(
            customMessage: $"{commandType.Name} must expose a public SpaceId property so the middleware can reach it");

        prop.CanWrite.ShouldBeTrue(
            customMessage: $"{commandType.Name}.SpaceId must be writable — the middleware sets it via reflection");

        prop.PropertyType.ShouldBe(
            typeof(int?),
            customMessage:
                $"{commandType.Name}.SpaceId must be int? exactly. The middleware checks " +
                "PropertyType != typeof(int?) and silently skips anything else (int, long?, etc.) — " +
                "re-opening the body-drift vector.");
    }
}

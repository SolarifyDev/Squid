namespace Squid.Core.Services.Machines.Exceptions;

/// <summary>
/// Thrown when a machine registration is attempted with a name that already
/// exists in the same space. Mapped to HTTP 409 Conflict by the
/// <c>GlobalExceptionFilter</c>, which allows the Tentacle client to distinguish
/// "valid credentials, just the desired name is already taken" from "credentials
/// rejected" (401) and transient server errors (5xx).
///
/// Before this type existed the conflict surfaced as a generic
/// <see cref="InvalidOperationException"/> that the filter mapped to
/// <c>HttpStatusCode.InternalServerError</c> inside an HTTP 200 envelope — the
/// Tentacle client then mistook the response for success and attempted to poll
/// with a thumbprint the server never actually persisted, producing a
/// confusing "trust rejected" handshake loop.
/// </summary>
public sealed class MachineNameConflictException(string machineName, int spaceId)
    : InvalidOperationException($"A machine named \"{machineName}\" already exists in this space")
{
    public string MachineName { get; } = machineName;

    public int SpaceId { get; } = spaceId;
}

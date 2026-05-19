using System.Net;

namespace Squid.Tentacle.Registration;

/// <summary>
/// Thrown when the Squid Server rejects a register call with HTTP 403 +
/// a structured permission-denial body (containing <c>missingPermission</c>
/// + <c>suggestedRoles</c>).
///
/// <para>The Tentacle CLI's exception handler catches this type explicitly
/// and emits a multi-line operator-facing hint (missing permission name +
/// list of built-in roles that grant it), then exits with code <c>403</c>.
/// The install script's exit-code check then sees the 403 and propagates
/// the same hint to the operator who copy-pasted the snippet.</para>
///
/// <para>This is a separate type from <see cref="HttpRequestException"/>
/// so the entry-point catch can branch on type rather than parsing
/// inner message strings — the latter would silently break the moment
/// anyone reformats the message.</para>
/// </summary>
public sealed class PermissionDeniedRegistrationException : HttpRequestException
{
    public PermissionDeniedRegistrationException(string missingPermission, IReadOnlyList<string> suggestedRoles, string message)
        : base(message, null, HttpStatusCode.Forbidden)
    {
        MissingPermission = missingPermission ?? string.Empty;
        SuggestedRoles = suggestedRoles ?? Array.Empty<string>();
    }

    public string MissingPermission { get; }

    public IReadOnlyList<string> SuggestedRoles { get; }
}

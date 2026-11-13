using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>
/// A failed call to an Octopus server. The message is fixed per <see cref="Kind"/> and never carries the host,
/// the API key, a response body or an inner exception, because the API surfaces it to the caller verbatim.
/// </summary>
public sealed class OctopusRestException : InvalidOperationException
{
    public OctopusRestException(OctopusRestErrorKind kind) : base(MessageFor(kind))
    {
        Kind = kind;
    }

    /// <summary>A limit was exceeded; the message names the limit (an operator setting, never request data).</summary>
    public OctopusRestException(OctopusRestErrorKind kind, string limitName) : base($"{MessageFor(kind)} Limit: {limitName}.")
    {
        Kind = kind;
        LimitName = limitName;
    }

    public OctopusRestErrorKind Kind { get; }

    public string LimitName { get; }

    public static string MessageFor(OctopusRestErrorKind kind)
        => kind switch
        {
            OctopusRestErrorKind.Unauthorized => "The Octopus server rejected the API key, or the key lacks permission to read this data.",
            OctopusRestErrorKind.NotOctopus => "The server did not answer like an Octopus Deploy server. Check the server URL.",
            OctopusRestErrorKind.Unreachable => "The Octopus server could not be reached or kept failing. Try again later.",
            OctopusRestErrorKind.TooLarge => "The Octopus server returned more data than the configured limit allows.",
            OctopusRestErrorKind.Denied => "The Octopus server address or request is not allowed by this Squid server's policy.",
            OctopusRestErrorKind.NotFound => "The Octopus server does not have the requested item.",
            OctopusRestErrorKind.Rejected => "The Octopus server refused to return an item; it was skipped.",
            _ => "The Octopus server request failed."
        };
}

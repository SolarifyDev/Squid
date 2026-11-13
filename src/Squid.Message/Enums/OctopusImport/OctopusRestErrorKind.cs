namespace Squid.Message.Enums.OctopusImport;

/// <summary>Why a call to an Octopus server's REST API failed. Each kind carries a fixed, secret-free message.</summary>
public enum OctopusRestErrorKind
{
    Unauthorized = 0,
    NotOctopus = 1,
    Unreachable = 2,
    TooLarge = 3,
    Denied = 4,
    NotFound = 5,

    /// <summary>The server refused one item (400/409/422, or 403 on anything but the key's own identity); the item is skipped, the fetch goes on.</summary>
    Rejected = 6
}

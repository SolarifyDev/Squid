namespace Squid.Message.Enums.OctopusImport;

/// <summary>Why a call to an Octopus server's REST API failed. Each kind carries a fixed, secret-free message.</summary>
public enum OctopusRestErrorKind
{
    Unauthorized = 0,
    NotOctopus = 1,
    Unreachable = 2,
    TooLarge = 3,
    Denied = 4,
    NotFound = 5
}

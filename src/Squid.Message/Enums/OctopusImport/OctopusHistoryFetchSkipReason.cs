namespace Squid.Message.Enums.OctopusImport;

/// <summary>Why a REST fetch left an item out of the bundle. Append-only: SquidWeb mirrors it by ordinal.</summary>
public enum OctopusHistoryFetchSkipReason
{
    /// <summary>The Octopus server refused the request for this item (<c>OctopusRestErrorKind.Rejected</c>).</summary>
    Rejected = 0,

    /// <summary>The item outlived the chunk deadline too many times in a row.</summary>
    Abandoned = 1,

    /// <summary>The server kept answering one of the item's requests with an error status (5xx, or a status it should not return) in too many chunks in a row.</summary>
    ServerError = 2
}

namespace Squid.Message.Models.OctopusImport;

/// <summary>One chunk of a REST history fetch into an owned, still-Uploaded import session.</summary>
public sealed record OctopusHistoryFetchRequest(Guid SessionId, int SpaceId, string ServerUrl, string ApiKey, string OctopusSpaceId);

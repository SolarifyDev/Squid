namespace Squid.Message.Models.OctopusImport;

/// <summary>A live Octopus → Squid mapping recorded by an earlier confirmed import.</summary>
public sealed record OctopusImportPriorMapping(int DestinationId, string OutcomeState, string Fingerprint, string DetailFingerprint);

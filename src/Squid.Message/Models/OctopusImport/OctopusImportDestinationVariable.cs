namespace Squid.Message.Models.OctopusImport;

/// <summary>A variable a Squid variable set holds now, by name only (never its value).</summary>
public sealed record OctopusImportDestinationVariable(int VariableSetId, string Name);

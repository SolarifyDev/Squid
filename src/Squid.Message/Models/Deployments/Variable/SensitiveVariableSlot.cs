namespace Squid.Message.Models.Deployments.Variable;

/// <summary>One sensitive variable of a set as far as filling it is concerned: where it is, who it is, and whether it holds a value (never the value).</summary>
public sealed record SensitiveVariableSlot(int VariableId, int VariableSetId, VariableIdentity Identity, bool HasValue);

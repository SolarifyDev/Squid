namespace Squid.Calamari.Variables;

public sealed record VariableEntry(
    string Name,
    string Value,
    bool IsSensitive = false,
    string? Source = null);

namespace Squid.Calamari.Host;

public sealed class CommandDescriptor
{
    public CommandDescriptor(
        string name,
        string usage,
        string description,
        IEnumerable<string>? aliases = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Command name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(usage))
            throw new ArgumentException("Command usage is required.", nameof(usage));

        Name = name;
        Usage = usage;
        Description = description ?? string.Empty;
        Aliases = aliases?
            .Where(static a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? [];
    }

    public string Name { get; }

    public string Usage { get; }

    public string Description { get; }

    public IReadOnlyList<string> Aliases { get; }
}

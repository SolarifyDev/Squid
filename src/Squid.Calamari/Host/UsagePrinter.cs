namespace Squid.Calamari.Host;

public static class UsagePrinter
{
    public static void PrintCommand(CommandDescriptor descriptor, TextWriter writer)
    {
        if (descriptor is null)
            throw new ArgumentNullException(nameof(descriptor));
        if (writer is null)
            throw new ArgumentNullException(nameof(writer));

        writer.WriteLine($"Usage: squid-calamari {descriptor.Usage}");
        if (!string.IsNullOrWhiteSpace(descriptor.Description))
            writer.WriteLine(descriptor.Description);
    }

    public static void Print(CommandRegistry registry, TextWriter writer)
    {
        if (registry is null)
            throw new ArgumentNullException(nameof(registry));
        if (writer is null)
            throw new ArgumentNullException(nameof(writer));

        writer.WriteLine("squid-calamari <subcommand> [options]");
        writer.WriteLine("Use --help for command help.");
        writer.WriteLine();
        writer.WriteLine("Subcommands:");

        foreach (var handler in registry.Handlers)
            writer.WriteLine($"  {handler.Descriptor.Usage}");
    }
}

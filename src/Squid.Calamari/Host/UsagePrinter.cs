namespace Squid.Calamari.Host;

public static class UsagePrinter
{
    public static void Print(CommandRegistry registry, TextWriter writer)
    {
        if (registry is null)
            throw new ArgumentNullException(nameof(registry));
        if (writer is null)
            throw new ArgumentNullException(nameof(writer));

        writer.WriteLine("squid-calamari <subcommand> [options]");
        writer.WriteLine();
        writer.WriteLine("Subcommands:");

        foreach (var handler in registry.Handlers)
            writer.WriteLine($"  {handler.Descriptor.Usage}");
    }
}

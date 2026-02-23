namespace Squid.Calamari.Host;

public static class CommandLineArguments
{
    public static Dictionary<string, string> ParseKeyValueArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                continue;

            var eqIndex = arg.IndexOf('=', StringComparison.Ordinal);
            if (eqIndex < 0)
                continue;

            var key = arg[..eqIndex];
            var value = arg[(eqIndex + 1)..];
            result[key] = value;
        }

        return result;
    }
}

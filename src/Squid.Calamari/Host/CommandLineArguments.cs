namespace Squid.Calamari.Host;

public static class CommandLineArguments
{
    public static Dictionary<string, string> ParseKeyValueArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                continue;

            var eqIndex = arg.IndexOf('=', StringComparison.Ordinal);
            string key;
            string value;

            if (eqIndex >= 0)
            {
                key = arg[..eqIndex];
                value = arg[(eqIndex + 1)..];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                key = arg;
                value = args[++i];
            }
            else
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    public static bool IsHelpToken(string arg)
        => string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "help", StringComparison.OrdinalIgnoreCase);

    public static bool ContainsHelpToken(IEnumerable<string> args)
        => args.Any(IsHelpToken);
}

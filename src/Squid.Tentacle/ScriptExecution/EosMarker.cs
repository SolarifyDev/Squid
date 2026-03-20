namespace Squid.Tentacle.ScriptExecution;

public static class EosMarker
{
    public const string Prefix = "EOS-";
    public const string Separator = "<<>>";

    public static string GenerateMarkerToken()
        => Guid.NewGuid().ToString("N");

    public static string WrapScript(string scriptBody, string markerToken)
    {
        return scriptBody + "\n"
            + "__squid_exit_code__=$?\n"
            + $"echo \"{Prefix}{markerToken}{Separator}${{__squid_exit_code__}}\"\n"
            + "exit $__squid_exit_code__\n";
    }

    public static EosParseResult? TryParse(string line, string markerToken)
    {
        if (string.IsNullOrEmpty(line)) return null;

        var expectedPrefix = $"{Prefix}{markerToken}{Separator}";

        if (!line.StartsWith(expectedPrefix, StringComparison.Ordinal))
            return null;

        var exitCodeStr = line[expectedPrefix.Length..];

        if (!int.TryParse(exitCodeStr, out var exitCode))
            return null;

        return new EosParseResult(exitCode);
    }
}

public record EosParseResult(int ExitCode);

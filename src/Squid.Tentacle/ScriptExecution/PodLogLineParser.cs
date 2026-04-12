using System.Text.RegularExpressions;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Tentacle.ScriptExecution;

public static class PodLogLineParser
{
    private static readonly Regex DirectivePattern = new(
        @"^##squid\[(?<type>\S+?)(?:\s+(?<args>.+))?\]$",
        RegexOptions.Compiled);

    private static readonly Regex ArgPattern = new(
        @"(?<key>\w+)='(?<value>[^']*)'",
        RegexOptions.Compiled);

    public static ParsedLogLine Parse(string line)
    {
        if (string.IsNullOrEmpty(line))
            return new ParsedLogLine(ProcessOutputSource.StdOut, line, false, null, null);

        var match = DirectivePattern.Match(line);

        if (!match.Success)
            return new ParsedLogLine(ProcessOutputSource.StdOut, line, false, null, null);

        var directiveType = match.Groups["type"].Value;
        var argsString = match.Groups["args"].Value;
        var args = ParseArgs(argsString);

        DecodeBase64Values(args);

        var source = ResolveSource(directiveType);

        return new ParsedLogLine(source, line, true, directiveType, args);
    }

    private static ProcessOutputSource ResolveSource(string directiveType)
    {
        return directiveType switch
        {
            "stdout-error" => ProcessOutputSource.StdErr,
            "stdout-warning" => ProcessOutputSource.StdErr,
            _ => ProcessOutputSource.StdOut
        };
    }

    private static Dictionary<string, string> ParseArgs(string argsString)
    {
        if (string.IsNullOrWhiteSpace(argsString))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in ArgPattern.Matches(argsString))
        {
            result[m.Groups["key"].Value] = m.Groups["value"].Value;
        }

        return result;
    }

    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static void DecodeBase64Values(Dictionary<string, string> args)
    {
        var keys = args.Keys.ToList();

        foreach (var key in keys)
        {
            var value = args[key];

            if (string.IsNullOrEmpty(value)) continue;

            try
            {
                var bytes = Convert.FromBase64String(value);
                var decoded = StrictUtf8.GetString(bytes);

                if (!IsReadableText(decoded)) continue;

                args[key] = decoded;
            }
            catch (FormatException)
            {
                // Not base64 — keep raw value
            }
            catch (System.Text.DecoderFallbackException)
            {
                // Valid base64 but not valid UTF-8 — keep raw value
            }
        }
    }

    private static bool IsReadableText(string s)
    {
        return s.All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t');
    }
}

public sealed class ParsedLogLine
{
    public ParsedLogLine(ProcessOutputSource source, string text, bool isDirective, string directiveType, Dictionary<string, string> directiveArgs)
    {
        Source = source;
        Text = text;
        IsDirective = isDirective;
        DirectiveType = directiveType;
        DirectiveArgs = directiveArgs ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public ProcessOutputSource Source { get; }

    public string Text { get; }

    public bool IsDirective { get; }

    public string DirectiveType { get; }

    public Dictionary<string, string> DirectiveArgs { get; }
}

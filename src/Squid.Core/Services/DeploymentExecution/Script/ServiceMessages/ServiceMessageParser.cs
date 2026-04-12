using System.Text;
using System.Text.RegularExpressions;

namespace Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;

public sealed partial class ServiceMessageParser : IServiceMessageParser
{
    private const string SquidPrefix = "##squid[";

    // Matches the full envelope: ##squid[verb <attrs>]
    private static readonly Regex EnvelopeRegex = BuildEnvelopeRegex();

    [GeneratedRegex(@"^##squid\[(?<verb>[A-Za-z][A-Za-z0-9]*)\s*(?<attrs>[^\]]*)\]$", RegexOptions.Compiled)]
    private static partial Regex BuildEnvelopeRegex();

    // Matches key="base64value" (double quotes = base64-encoded) or key='plain value' (single quotes = plaintext)
    private static readonly Regex AttributeRegex = BuildAttributeRegex();

    [GeneratedRegex(@"(?<key>[A-Za-z][A-Za-z0-9_]*)=(?:""(?<b64>[^""]*)""|'(?<plain>[^']*)')", RegexOptions.Compiled)]
    private static partial Regex BuildAttributeRegex();

    public Dictionary<string, OutputVariable> ParseOutputVariables(IEnumerable<string> logLines)
    {
        var result = new Dictionary<string, OutputVariable>(StringComparer.OrdinalIgnoreCase);

        if (logLines == null)
            return result;

        foreach (var line in logLines)
        {
            var variable = TryParseOutputVariable(line);
            if (variable == null)
                continue;

            result[variable.Name] = variable;
        }

        return result;
    }

    public IReadOnlyList<ParsedServiceMessage> ParseMessages(IEnumerable<string> logLines)
    {
        var result = new List<ParsedServiceMessage>();

        if (logLines == null)
            return result;

        foreach (var line in logLines)
        {
            var message = TryParseMessage(line);
            if (message == null)
                continue;

            result.Add(message);
        }

        return result;
    }

    public ParsedServiceMessage TryParseMessage(string line)
    {
        if (!IsServiceMessage(line))
            return null;

        var envelope = EnvelopeRegex.Match(line);
        if (!envelope.Success)
            return null;

        var verb = envelope.Groups["verb"].Value;
        var attributes = ExtractAttributes(envelope.Groups["attrs"].Value);
        if (attributes == null)
            return null;

        return new ParsedServiceMessage(MapKind(verb), verb, attributes);
    }

    public OutputVariable TryParseOutputVariable(string line)
    {
        var message = TryParseMessage(line);
        if (message == null || message.Kind != ServiceMessageKind.SetVariable)
            return null;

        var name = message.GetAttribute("name");
        if (string.IsNullOrEmpty(name))
            return null;

        var value = message.GetAttribute("value") ?? string.Empty;
        var sensitive = string.Equals(message.GetAttribute("sensitive"), "True", StringComparison.OrdinalIgnoreCase);

        return new OutputVariable(name, value, sensitive);
    }

    private static bool IsServiceMessage(string line)
        => !string.IsNullOrEmpty(line) && line.StartsWith(SquidPrefix, StringComparison.Ordinal);

    private static ServiceMessageKind MapKind(string verb) => verb switch
    {
        "setVariable" => ServiceMessageKind.SetVariable,
        "createArtifact" => ServiceMessageKind.CreateArtifact,
        "stepFailed" => ServiceMessageKind.StepFailed,
        "stdWarning" => ServiceMessageKind.StdWarning,
        _ => ServiceMessageKind.Unknown
    };

    private static IReadOnlyDictionary<string, string> ExtractAttributes(string attrBlock)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(attrBlock))
            return dict;

        foreach (Match match in AttributeRegex.Matches(attrBlock))
        {
            var key = match.Groups["key"].Value;

            if (match.Groups["b64"].Success)
            {
                if (!TryDecodeBase64(match.Groups["b64"].Value, out var decoded))
                    return null;

                dict[key] = decoded;
                continue;
            }

            dict[key] = match.Groups["plain"].Value;
        }

        return dict;
    }

    private static bool TryDecodeBase64(string input, out string decoded)
    {
        decoded = null;

        if (input == null)
            return false;

        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(input));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record OutputVariable(string Name, string Value, bool IsSensitive);

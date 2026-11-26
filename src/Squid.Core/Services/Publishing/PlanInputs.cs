using System.Globalization;
using Squid.Core.Services.Publishing.Exceptions;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

/// <summary>Checks the values the publish request gave against the inputs the plan declared, by the type each declares: no recipe needs anything added here.</summary>
public static class PlanInputs
{
    /// <summary>The value of every declared input, trimmed. Throws <see cref="RecipeInputInvalidException"/> for the first one that is missing or of the wrong type.</summary>
    public static Dictionary<string, string> Validate(DeploymentPlan plan, IReadOnlyDictionary<string, string> given)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var input in plan.Inputs)
            values[input.Name] = Validate(input, Lookup(given, input.Name));

        return values;
    }

    private static string Lookup(IReadOnlyDictionary<string, string> given, string name)
    {
        if (given == null) return null;

        foreach (var pair in given)
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair.Value;

        return null;
    }

    private static string Validate(PlanInput input, string value)
    {
        return input.Type switch
        {
            PlanInputType.TcpPort => ValidatePort(input, value),
            _ => throw new InvalidOperationException($"The plan declares the input '{input.Name}' with the type {input.Type}, which publishing does not know how to check.")
        };
    }

    private static string ValidatePort(PlanInput input, string value)
    {
        if (int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535)
            return port.ToString(CultureInfo.InvariantCulture);

        throw new RecipeInputInvalidException(input.Name, $"give a TCP port between 1 and 65535 ({input.Description})");
    }
}

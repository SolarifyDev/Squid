using Squid.Core.VariableSubstitution.Templates.Functions;

namespace Squid.Core.VariableSubstitution.Templates
{
    static class BuiltInFunctions
    {
        static readonly IDictionary<string, Func<string, string[], string>> extensions = new Dictionary<string, Func<string, string[], string>>(StringComparer.OrdinalIgnoreCase)
        {
            {"tolower", TextCaseFunction.ToLower },
            {"toupper", TextCaseFunction.ToUpper },
            {"htmlescape", TextEscapeFunction.HtmlEscape },
            {"xmlescape", TextEscapeFunction.XmlEscape },
            {"jsonescape", TextEscapeFunction.JsonEscape },
            {"nowdate", DateFunction.NowDate },
            {"nowdateutc", DateFunction.NowDateUtc },
            {"format", FormatFunction.Format }
        };

        public static void Register(string name, Func<string, string[], string> implementation)
        {
            var functionName = name.ToLowerInvariant();

            if(!extensions.ContainsKey(functionName))
                extensions.Add(functionName, implementation);
        }

        public static string InvokeOrNull(string function, string argument, string[] options)
        {
            var functionName = function.ToLowerInvariant();

            Func<string, string[], string> ext;
            if (extensions.TryGetValue(functionName, out ext))
                return ext(argument, options);

            return null;
        }
    }
}

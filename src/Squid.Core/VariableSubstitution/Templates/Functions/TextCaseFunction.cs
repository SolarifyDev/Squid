using System.Linq;

namespace Squid.Core.VariableSubstitution.Templates.Functions
{
    internal class TextCaseFunction
    {
        public static string ToUpper(string argument, string[] options)
        {
            return options.Any() ? null : argument?.ToUpper();
        }

        public static string ToLower(string argument, string[] options)
        {
            return options.Any() ? null : argument?.ToLower();
        }
    }
}

using System.Text;

namespace Squid.Core.VariableSubstitution.Templates.Functions
{
    /// <summary>
    /// Octostache's <c>ToBase64</c> filter (UTF-8). Returns null — leaving the token unresolved, like every
    /// other filter — for a missing value or any option, since only the default encoding is supported.
    /// </summary>
    internal static class Base64Function
    {
        public static string ToBase64(string argument, string[] options)
        {
            if (argument == null || options.Any()) return null;

            return Convert.ToBase64String(Encoding.UTF8.GetBytes(argument));
        }
    }
}

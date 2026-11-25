using Squid.Core.Services.Publishing.Exceptions;

namespace Squid.Core.Services.Publishing.Recipes;

/// <summary>
/// Puts a value into a bash script as one literal word. Single quotes keep bash from expanding anything inside, and a single quote in the value is
/// closed, escaped and reopened. A value with <c>#{</c> is refused: the deployment would read it as a variable and substitute it into the script.
/// </summary>
public static class ShellQuote
{
    public static string Literal(string input, string value)
    {
        value ??= string.Empty;

        if (value.Contains("#{", StringComparison.Ordinal))
            throw new RecipeInputInvalidException(input, "it contains '#{', which deployments read as a variable");

        if (value.Contains('\0') || value.Contains('\n') || value.Contains('\r'))
            throw new RecipeInputInvalidException(input, "it contains a line break or a null character");

        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}

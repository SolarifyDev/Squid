namespace Squid.Core.Services.Publishing.Exceptions;

/// <summary>A recipe cannot render because a value it needs is missing or unusable: a fact the package does not have, or a field the publish request left out.</summary>
public sealed class RecipeInputInvalidException(string input, string reason) : InvalidOperationException($"The input '{input}' is missing or invalid: {reason}")
{
    public string Input { get; } = input;

    public string Reason { get; } = reason;
}

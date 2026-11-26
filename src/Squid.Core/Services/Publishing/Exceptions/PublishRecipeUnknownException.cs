namespace Squid.Core.Services.Publishing.Exceptions;

/// <summary>The requested recipe (in the request, or named by <c>squid.yaml</c>) does not exist.</summary>
public sealed class PublishRecipeUnknownException(string requested, IReadOnlyList<string> validKinds) : InvalidOperationException($"There is no recipe '{requested}'. The recipes are: {string.Join(", ", validKinds)}.")
{
    public string Requested { get; } = requested;

    public IReadOnlyList<string> ValidKinds { get; } = validKinds;
}

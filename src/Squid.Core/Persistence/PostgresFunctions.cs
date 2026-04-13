namespace Squid.Core.Persistence;

/// <summary>
/// Maps PostgreSQL's <c>jsonb_extract_path_text</c> function for use in LINQ queries.
/// Extracts a text value from a JSONB column by key — equivalent to the <c>-&gt;&gt;</c> operator.
/// </summary>
/// <example>
/// <code>
/// // In LINQ:
/// .Where(m => PostgresFunctions.JsonValue(m.Endpoint, "Uri") == uri)
///
/// // Translates to SQL:
/// WHERE jsonb_extract_path_text(endpoint, 'Uri') = @p0
/// </code>
/// </example>
public static class PostgresFunctions
{
    /// <summary>
    /// Extracts a text value from a JSONB column at the specified key.
    /// This method is translated to SQL by EF Core and must not be called directly.
    /// </summary>
    public static string JsonValue(string column, string key)
        => throw new NotSupportedException("This method is translated to SQL by EF Core and cannot be called directly.");
}

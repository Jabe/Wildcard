namespace Wildcard;

/// <summary>
/// LINQ-style extension methods for filtering collections with wildcard patterns.
/// </summary>
public static class WildcardExtensions
{
    /// <summary>
    /// Filters a sequence of strings, returning only those that match the wildcard pattern.
    /// </summary>
    public static IEnumerable<string> WhereMatch(this IEnumerable<string> source, WildcardPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pattern);

        foreach (var item in source)
        {
            if (pattern.IsMatch(item))
                yield return item;
        }
    }

    /// <summary>
    /// Filters a sequence of strings, returning only those that match the wildcard pattern string.
    /// The pattern is compiled once and reused for all items.
    /// </summary>
    public static IEnumerable<string> WhereMatch(this IEnumerable<string> source, string pattern, bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pattern);

        var compiled = WildcardPattern.Compile(pattern, ignoreCase);
        foreach (var item in source)
        {
            if (compiled.IsMatch(item))
                yield return item;
        }
    }

    /// <summary>
    /// Returns true if any string in the sequence matches the wildcard pattern.
    /// </summary>
    public static bool AnyMatch(this IEnumerable<string> source, WildcardPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pattern);

        foreach (var item in source)
        {
            if (pattern.IsMatch(item))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the first string that matches the wildcard pattern, or null if none match.
    /// </summary>
    public static string? FirstMatch(this IEnumerable<string> source, WildcardPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pattern);

        foreach (var item in source)
        {
            if (pattern.IsMatch(item))
                return item;
        }
        return null;
    }
}

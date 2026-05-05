namespace Wildcard;

/// <summary>
/// A structured representation of a wildcard pattern that can be translated
/// to SQL predicates, LINQ expressions, or other query languages.
/// </summary>
public abstract class PatternPredicate
{
    /// <summary>Whether the match should be case-insensitive.</summary>
    public bool IgnoreCase { get; }

    private PatternPredicate(bool ignoreCase) => IgnoreCase = ignoreCase;

    /// <summary>Exact string equality. SQL: <c>column = 'value'</c></summary>
    public sealed class Exact(string value, bool ignoreCase = false) : PatternPredicate(ignoreCase)
    {
        public string Value { get; } = value;
    }

    /// <summary>Prefix match. SQL: <c>column LIKE 'prefix%'</c></summary>
    public sealed class StartsWith(string prefix, bool ignoreCase = false) : PatternPredicate(ignoreCase)
    {
        public string Prefix { get; } = prefix;
    }

    /// <summary>Suffix match. SQL: <c>column LIKE '%suffix'</c></summary>
    public sealed class EndsWith(string suffix, bool ignoreCase = false) : PatternPredicate(ignoreCase)
    {
        public string Suffix { get; } = suffix;
    }

    /// <summary>Contains match. SQL: <c>column LIKE '%value%'</c></summary>
    public sealed class Contains(string value, bool ignoreCase = false) : PatternPredicate(ignoreCase)
    {
        public string Value { get; } = value;
    }

    /// <summary>Prefix and suffix match. SQL: <c>column LIKE 'prefix%suffix'</c></summary>
    public sealed class StartsAndEndsWith(string prefix, string suffix, bool ignoreCase = false) : PatternPredicate(ignoreCase)
    {
        public string Prefix { get; } = prefix;
        public string Suffix { get; } = suffix;
    }

    /// <summary>
    /// Complex pattern that cannot be expressed as a simple LIKE clause.
    /// Provides a regex pattern string for database engines that support REGEXP.
    /// </summary>
    public sealed class Regex(string pattern, bool ignoreCase = false) : PatternPredicate(ignoreCase)
    {
        public string Pattern { get; } = pattern;
    }

    /// <summary>
    /// A pattern expressible as a SQL LIKE clause using <c>%</c> (any sequence) and <c>_</c> (single char).
    /// Produced when a pattern uses multiple wildcards (* and ?) but no character classes or braces.
    /// SQL: <c>column LIKE '%vlc%winarm64.exe'</c>.
    /// </summary>
    public sealed class Like(string likePattern, bool ignoreCase = false) : PatternPredicate(ignoreCase)
    {
        /// <summary>The LIKE expression string (e.g. <c>%vlc%winarm64.exe</c>).</summary>
        public string LikePattern { get; } = likePattern;
    }

    /// <summary>
    /// Disjunction of multiple predicates (OR semantics).
    /// Produced by brace alternation patterns like <c>{error,warn}: *</c>.
    /// SQL: <c>(column LIKE 'error: %' OR column LIKE 'warn: %')</c>.
    /// </summary>
    public sealed class AnyOf(PatternPredicate[] alternatives, bool ignoreCase = false) : PatternPredicate(ignoreCase)
    {
        public PatternPredicate[] Alternatives { get; } = alternatives;
    }
}

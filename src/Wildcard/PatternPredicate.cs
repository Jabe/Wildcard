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
    /// A pattern expressible as a SQL LIKE clause. Stored as an ordered sequence of <see cref="LikePart"/>
    /// so renderers can apply dialect-specific escape rules (T-SQL/Cosmos bracket-escape vs. Postgres backslash-escape).
    /// Produced when a pattern uses multiple wildcards (* and ?) but no character classes.
    /// </summary>
    public sealed class Like(LikePart[] parts, bool ignoreCase = false) : PatternPredicate(ignoreCase)
    {
        /// <summary>The ordered parts of the LIKE pattern.</summary>
        public LikePart[] Parts { get; } = parts;
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

/// <summary>
/// A single component of a <see cref="PatternPredicate.Like"/> pattern.
/// Discriminated union over <see cref="AnySequence"/>, <see cref="AnySingle"/>, and <see cref="Literal"/>.
/// </summary>
public abstract class LikePart
{
    private LikePart() { }

    /// <summary>Matches any sequence of zero or more characters (rendered as <c>%</c>).</summary>
    public sealed class AnySequence : LikePart
    {
        /// <summary>Singleton instance.</summary>
        public static readonly AnySequence Instance = new();
    }

    /// <summary>Matches a fixed run of single characters (rendered as <c>_</c> repeated <see cref="Count"/> times).</summary>
    public sealed class AnySingle(int count) : LikePart
    {
        /// <summary>Number of consecutive single-character placeholders.</summary>
        public int Count { get; } = count;
    }

    /// <summary>A literal substring. The renderer escapes any LIKE-special characters per dialect.</summary>
    public sealed class Literal(string value) : LikePart
    {
        /// <summary>The unescaped literal value.</summary>
        public string Value { get; } = value;
    }
}

using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Wildcard;

/// <summary>
/// Renders <see cref="PatternPredicate"/> values as <see cref="Expression{T}"/> filters
/// usable with <c>IQueryable.Where</c>, EF Core, in-memory LINQ, and any other LINQ
/// provider. Predicates map to idiomatic <see cref="string"/> methods (<c>Equals</c>,
/// <c>StartsWith</c>, <c>EndsWith</c>, <c>Contains</c>) so EF Core translates them
/// natively to LIKE on whatever backend the DbContext targets.
/// </summary>
public static class PatternPredicateLinqExtensions
{
    private static readonly MethodInfo StringEqualsCmp =
        Method<string>(nameof(string.Equals), [typeof(string), typeof(StringComparison)]);
    private static readonly MethodInfo StringStartsWith =
        Method<string>(nameof(string.StartsWith), [typeof(string)]);
    private static readonly MethodInfo StringStartsWithCmp =
        Method<string>(nameof(string.StartsWith), [typeof(string), typeof(StringComparison)]);
    private static readonly MethodInfo StringEndsWith =
        Method<string>(nameof(string.EndsWith), [typeof(string)]);
    private static readonly MethodInfo StringEndsWithCmp =
        Method<string>(nameof(string.EndsWith), [typeof(string), typeof(StringComparison)]);
    private static readonly MethodInfo StringContains =
        Method<string>(nameof(string.Contains), [typeof(string)]);
    private static readonly MethodInfo StringContainsCmp =
        Method<string>(nameof(string.Contains), [typeof(string), typeof(StringComparison)]);

    private static readonly MethodInfo RegexIsMatchStatic = typeof(Regex).GetMethod(
        nameof(Regex.IsMatch),
        BindingFlags.Public | BindingFlags.Static,
        binder: null,
        types: [typeof(string), typeof(string), typeof(RegexOptions)],
        modifiers: null) ?? throw new InvalidOperationException("Regex.IsMatch(string, string, RegexOptions) not found.");

    private static MethodInfo Method<T>(string name, Type[] types) =>
        typeof(T).GetMethod(name, types) ?? throw new InvalidOperationException($"{typeof(T).Name}.{name} not found.");

    /// <summary>
    /// Compiles this predicate into an <see cref="Expression{T}"/> that tests the property
    /// projected by <paramref name="propertyAccessor"/>. Use with <c>IQueryable.Where</c>
    /// or call <c>.Compile()</c> for in-memory filtering.
    /// </summary>
    /// <param name="predicate">The predicate to compile.</param>
    /// <param name="propertyAccessor">Selector for the string property to test (e.g. <c>i =&gt; i.Name</c>).</param>
    /// <remarks>
    /// The property must be non-null at evaluation time when used in-memory; instance string
    /// methods will throw <see cref="NullReferenceException"/> on null. EF Core translates
    /// these to SQL where NULL handling is automatic.
    /// <para>
    /// <see cref="PatternPredicate.Like"/> and <see cref="PatternPredicate.Regex"/> compile to
    /// <see cref="Regex.IsMatch(string, string, RegexOptions)"/>. EF Core translation of
    /// <c>Regex.IsMatch</c> is provider-specific (Npgsql translates; SQL Server / SQLite / Cosmos
    /// generally do not). For those targets prefer the dialect-specific
    /// <see cref="PatternPredicateSqlExtensions.ToSql"/> renderer.
    /// </para>
    /// </remarks>
    public static Expression<Func<T, bool>> ToExpression<T>(
        this PatternPredicate predicate,
        Expression<Func<T, string>> propertyAccessor)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(propertyAccessor);

        var parameter = propertyAccessor.Parameters[0];
        var input = propertyAccessor.Body;
        var body = Build(predicate, input);
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    private static Expression Build(PatternPredicate pred, Expression input) => pred switch
    {
        PatternPredicate.Exact e             => BuildExact(e, input),
        PatternPredicate.StartsWith s        => BuildStartsWith(s.Prefix, s.IgnoreCase, input),
        PatternPredicate.EndsWith e          => BuildEndsWith(e.Suffix, e.IgnoreCase, input),
        PatternPredicate.Contains c          => BuildContains(c.Value, c.IgnoreCase, input),
        PatternPredicate.StartsAndEndsWith p => BuildStartsAndEndsWith(p, input),
        PatternPredicate.Like l              => BuildRegex(LikePartsToRegex(l.Parts), l.IgnoreCase, input),
        PatternPredicate.Regex r             => BuildRegex(r.Pattern, r.IgnoreCase, input),
        PatternPredicate.AnyOf any           => BuildAnyOf(any.Alternatives, input),
        _ => throw new NotSupportedException($"Unknown predicate type {pred.GetType().Name}"),
    };

    private static Expression BuildExact(PatternPredicate.Exact e, Expression input) =>
        e.IgnoreCase
            ? Expression.Call(input, StringEqualsCmp,
                Expression.Constant(e.Value),
                Expression.Constant(StringComparison.OrdinalIgnoreCase))
            : Expression.Equal(input, Expression.Constant(e.Value));

    private static Expression BuildStartsWith(string prefix, bool ignoreCase, Expression input) =>
        ignoreCase
            ? Expression.Call(input, StringStartsWithCmp,
                Expression.Constant(prefix),
                Expression.Constant(StringComparison.OrdinalIgnoreCase))
            : Expression.Call(input, StringStartsWith, Expression.Constant(prefix));

    private static Expression BuildEndsWith(string suffix, bool ignoreCase, Expression input) =>
        ignoreCase
            ? Expression.Call(input, StringEndsWithCmp,
                Expression.Constant(suffix),
                Expression.Constant(StringComparison.OrdinalIgnoreCase))
            : Expression.Call(input, StringEndsWith, Expression.Constant(suffix));

    private static Expression BuildContains(string value, bool ignoreCase, Expression input) =>
        ignoreCase
            ? Expression.Call(input, StringContainsCmp,
                Expression.Constant(value),
                Expression.Constant(StringComparison.OrdinalIgnoreCase))
            : Expression.Call(input, StringContains, Expression.Constant(value));

    private static Expression BuildStartsAndEndsWith(PatternPredicate.StartsAndEndsWith p, Expression input)
    {
        // Mirror the library's invariant: input.Length >= prefix.Length + suffix.Length
        // (prevents prefix and suffix from overlapping on short inputs).
        var sw = BuildStartsWith(p.Prefix, p.IgnoreCase, input);
        var ew = BuildEndsWith(p.Suffix, p.IgnoreCase, input);
        var lengthCheck = Expression.GreaterThanOrEqual(
            Expression.Property(input, nameof(string.Length)),
            Expression.Constant(p.Prefix.Length + p.Suffix.Length));
        return Expression.AndAlso(Expression.AndAlso(lengthCheck, sw), ew);
    }

    private static Expression BuildRegex(string pattern, bool ignoreCase, Expression input)
    {
        var options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        return Expression.Call(
            instance: null,
            RegexIsMatchStatic,
            input,
            Expression.Constant(pattern),
            Expression.Constant(options));
    }

    private static Expression BuildAnyOf(PatternPredicate[] alternatives, Expression input)
    {
        if (alternatives.Length == 0) return Expression.Constant(false);
        var expr = Build(alternatives[0], input);
        for (int i = 1; i < alternatives.Length; i++)
            expr = Expression.OrElse(expr, Build(alternatives[i], input));
        return expr;
    }

    private static string LikePartsToRegex(LikePart[] parts)
    {
        var sb = new StringBuilder("^");
        foreach (var p in parts)
        {
            switch (p)
            {
                case LikePart.AnySequence:                       sb.Append(".*"); break;
                case LikePart.AnySingle s when s.Count == 1:     sb.Append('.'); break;
                case LikePart.AnySingle s:                       sb.Append(".{").Append(s.Count).Append('}'); break;
                case LikePart.Literal l:                         sb.Append(Regex.Escape(l.Value)); break;
                default: throw new NotSupportedException($"Unknown LikePart {p.GetType().Name}");
            }
        }
        sb.Append('$');
        return sb.ToString();
    }
}

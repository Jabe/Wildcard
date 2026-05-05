namespace Wildcard;

internal abstract class SqlRenderer(string parameterPrefix)
{
    private readonly List<SqlParam> _params = [];
    private readonly string _prefix = parameterPrefix;

    public IReadOnlyList<SqlParam> Parameters => _params;

    public static SqlRenderer Create(SqlDialect dialect, string parameterPrefix) => dialect switch
    {
        SqlDialect.SqlServer => new SqlServerRenderer(parameterPrefix),
        SqlDialect.Postgres  => new PostgresRenderer(parameterPrefix),
        SqlDialect.CosmosSql => new CosmosSqlRenderer(parameterPrefix),
        SqlDialect.Sqlite    => new SqliteRenderer(parameterPrefix),
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unknown SQL dialect."),
    };

    protected string AddParam(object value)
    {
        var name = $"@{_prefix}{_params.Count}";
        _params.Add(new SqlParam(name, value));
        return name;
    }

    public string Render(PatternPredicate pred, string column) => pred switch
    {
        PatternPredicate.Exact e             => RenderExact(e.Value, e.IgnoreCase, column),
        PatternPredicate.StartsWith s        => RenderStartsWith(s.Prefix, s.IgnoreCase, column),
        PatternPredicate.EndsWith e          => RenderEndsWith(e.Suffix, e.IgnoreCase, column),
        PatternPredicate.Contains c          => RenderContains(c.Value, c.IgnoreCase, column),
        PatternPredicate.StartsAndEndsWith p => RenderStartsAndEndsWith(p.Prefix, p.Suffix, p.IgnoreCase, column),
        PatternPredicate.Like l              => RenderLike(l.Parts, l.IgnoreCase, column),
        PatternPredicate.Regex r             => RenderRegex(r.Pattern, r.IgnoreCase, column),
        PatternPredicate.AnyOf any           => RenderAnyOf(any.Alternatives, column),
        _ => throw new NotSupportedException($"Unknown predicate type {pred.GetType().Name}"),
    };

    protected abstract string RenderExact(string value, bool ignoreCase, string column);
    protected abstract string RenderStartsWith(string prefix, bool ignoreCase, string column);
    protected abstract string RenderEndsWith(string suffix, bool ignoreCase, string column);
    protected abstract string RenderContains(string value, bool ignoreCase, string column);
    protected abstract string RenderStartsAndEndsWith(string prefix, string suffix, bool ignoreCase, string column);
    protected abstract string RenderLike(LikePart[] parts, bool ignoreCase, string column);
    protected abstract string RenderRegex(string pattern, bool ignoreCase, string column);

    private string RenderAnyOf(PatternPredicate[] alternatives, string column)
    {
        if (alternatives.Length == 1) return Render(alternatives[0], column);
        var rendered = new string[alternatives.Length];
        for (int i = 0; i < alternatives.Length; i++)
            rendered[i] = Render(alternatives[i], column);
        return "(" + string.Join(" OR ", rendered) + ")";
    }
}

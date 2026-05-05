namespace Wildcard;

/// <summary>
/// SQL dialect target for <see cref="PatternPredicateSqlExtensions.ToSql"/>.
/// </summary>
public enum SqlDialect
{
    /// <summary>Microsoft SQL Server / Azure SQL (T-SQL). LIKE with bracket-escape; <c>REGEXP_LIKE</c> for regex (requires SQL Server 2025+ or Azure SQL with database compatibility level 170+).</summary>
    SqlServer,

    /// <summary>PostgreSQL. LIKE/ILIKE with backslash <c>ESCAPE</c>; POSIX regex via <c>~</c>/<c>~*</c>.</summary>
    Postgres,

    /// <summary>Azure Cosmos DB NoSQL. LIKE with bracket-escape; native STARTSWITH/ENDSWITH/CONTAINS/STRINGEQUALS with ignoreCase; REGEXMATCH for regex.</summary>
    CosmosSql,

    /// <summary>SQLite. Uses GLOB for case-sensitive matching and LIKE for case-insensitive (LIKE folds ASCII letters by default); COLLATE NOCASE for Exact CI; REGEXP requires the regexp extension to be registered on the connection.</summary>
    Sqlite,
}

/// <summary>A named SQL parameter binding emitted by <see cref="PatternPredicateSqlExtensions.ToSql"/>.</summary>
public readonly record struct SqlParam(string Name, object Value);

/// <summary>A rendered SQL boolean expression and its parameter bindings.</summary>
public readonly record struct SqlClause(string Sql, IReadOnlyList<SqlParam> Parameters)
{
    /// <inheritdoc />
    public override string ToString() => Sql;
}

/// <summary>
/// Renders <see cref="PatternPredicate"/> values as SQL boolean expressions for a target dialect.
/// </summary>
public static class PatternPredicateSqlExtensions
{
    /// <summary>
    /// Renders this predicate as a parameterized SQL boolean expression for the given <paramref name="dialect"/>.
    /// </summary>
    /// <param name="predicate">The predicate to render.</param>
    /// <param name="dialect">The target SQL dialect.</param>
    /// <param name="column">The column expression to test (e.g. <c>"c.name"</c> for Cosmos, <c>"[Name]"</c> for SQL Server). Inserted verbatim — escape if untrusted.</param>
    /// <param name="parameterPrefix">Prefix for generated parameter names; defaults to <c>"p"</c> producing <c>@p0</c>, <c>@p1</c>, ...</param>
    /// <returns>A <see cref="SqlClause"/> with the SQL expression and ordered parameter bindings.</returns>
    public static SqlClause ToSql(this PatternPredicate predicate, SqlDialect dialect, string column, string parameterPrefix = "p")
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(parameterPrefix);

        var renderer = SqlRenderer.Create(dialect, parameterPrefix);
        var sql = renderer.Render(predicate, column);
        return new SqlClause(sql, renderer.Parameters);
    }
}

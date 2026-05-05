using Wildcard;

namespace Wildcard.Tests;

public class PatternPredicateSqlTests
{
    // ── SQL Server ──

    [Fact]
    public void SqlServer_Exact()
    {
        var clause = WildcardPattern.ToPredicate("foo.cs").ToSql(SqlDialect.SqlServer, "filename");
        Assert.Equal("filename = @p0", clause.Sql);
        AssertParam(clause, "@p0", "foo.cs");
    }

    [Fact]
    public void SqlServer_ExactIgnoreCase()
    {
        var clause = WildcardPattern.ToPredicate("Foo.CS", ignoreCase: true).ToSql(SqlDialect.SqlServer, "filename");
        Assert.Equal("LOWER(filename) = @p0", clause.Sql);
        AssertParam(clause, "@p0", "foo.cs");
    }

    [Fact]
    public void SqlServer_StartsWith()
    {
        var clause = WildcardPattern.ToPredicate("foo*").ToSql(SqlDialect.SqlServer, "filename");
        Assert.Equal("filename LIKE @p0", clause.Sql);
        AssertParam(clause, "@p0", "foo%");
    }

    [Fact]
    public void SqlServer_EndsWith()
    {
        var clause = WildcardPattern.ToPredicate("*.cs").ToSql(SqlDialect.SqlServer, "filename");
        Assert.Equal("filename LIKE @p0", clause.Sql);
        AssertParam(clause, "@p0", "%.cs");
    }

    [Fact]
    public void SqlServer_Contains()
    {
        var clause = WildcardPattern.ToPredicate("*foo*").ToSql(SqlDialect.SqlServer, "filename");
        Assert.Equal("filename LIKE @p0", clause.Sql);
        AssertParam(clause, "@p0", "%foo%");
    }

    [Fact]
    public void SqlServer_StartsAndEndsWith()
    {
        var clause = WildcardPattern.ToPredicate("foo*bar").ToSql(SqlDialect.SqlServer, "filename");
        Assert.Equal("filename LIKE @p0", clause.Sql);
        AssertParam(clause, "@p0", "foo%bar");
    }

    [Fact]
    public void SqlServer_Like_BracketEscapesSpecialChars()
    {
        // pattern "100%*" → Literal("100%"), Star → "100[%]%"
        var clause = WildcardPattern.ToPredicate("100%*?").ToSql(SqlDialect.SqlServer, "filename");
        Assert.Equal("filename LIKE @p0", clause.Sql);
        AssertParam(clause, "@p0", "100[%]%_");
    }

    [Fact]
    public void SqlServer_LikeIgnoreCase_LowersBothSides()
    {
        var clause = WildcardPattern.ToPredicate("Foo*Bar?", ignoreCase: true).ToSql(SqlDialect.SqlServer, "filename");
        Assert.Equal("LOWER(filename) LIKE @p0", clause.Sql);
        AssertParam(clause, "@p0", "foo%bar_");
    }

    [Fact]
    public void SqlServer_StartsWithEscapesUnderscore()
    {
        var clause = WildcardPattern.ToPredicate("foo_bar*").ToSql(SqlDialect.SqlServer, "filename");
        AssertParam(clause, "@p0", "foo[_]bar%");
    }

    [Fact]
    public void SqlServer_RegexUsesRegexpLike()
    {
        var clause = WildcardPattern.ToPredicate("[abc]*").ToSql(SqlDialect.SqlServer, "filename");
        Assert.Equal("REGEXP_LIKE(filename, @p0)", clause.Sql);
        Assert.Single(clause.Parameters);
        Assert.StartsWith("^", (string)clause.Parameters[0].Value);
    }

    [Fact]
    public void SqlServer_RegexIgnoreCasePassesIFlag()
    {
        var clause = WildcardPattern.ToPredicate("[abc]*", ignoreCase: true).ToSql(SqlDialect.SqlServer, "filename");
        Assert.Equal("REGEXP_LIKE(filename, @p0, 'i')", clause.Sql);
    }

    [Fact]
    public void SqlServer_AnyOf_OrsClauses()
    {
        var clause = WildcardPattern.ToPredicate("{error,warn}: *").ToSql(SqlDialect.SqlServer, "msg");
        Assert.Equal("(msg LIKE @p0 OR msg LIKE @p1)", clause.Sql);
        AssertParam(clause, "@p0", "error: %");
        AssertParam(clause, "@p1", "warn: %");
    }

    // ── Postgres ──

    [Fact]
    public void Postgres_Exact()
    {
        var clause = WildcardPattern.ToPredicate("foo.cs").ToSql(SqlDialect.Postgres, "filename");
        Assert.Equal("filename = @p0", clause.Sql);
        AssertParam(clause, "@p0", "foo.cs");
    }

    [Fact]
    public void Postgres_ExactIgnoreCase_LowersBothSides()
    {
        var clause = WildcardPattern.ToPredicate("Foo.CS", ignoreCase: true).ToSql(SqlDialect.Postgres, "filename");
        Assert.Equal("LOWER(filename) = LOWER(@p0)", clause.Sql);
        AssertParam(clause, "@p0", "Foo.CS");
    }

    [Fact]
    public void Postgres_StartsWith()
    {
        var clause = WildcardPattern.ToPredicate("foo*").ToSql(SqlDialect.Postgres, "filename");
        Assert.Equal(@"filename LIKE @p0 ESCAPE '\'", clause.Sql);
        AssertParam(clause, "@p0", "foo%");
    }

    [Fact]
    public void Postgres_StartsWithIgnoreCase_UsesILike()
    {
        var clause = WildcardPattern.ToPredicate("foo*", ignoreCase: true).ToSql(SqlDialect.Postgres, "filename");
        Assert.Equal(@"filename ILIKE @p0 ESCAPE '\'", clause.Sql);
        AssertParam(clause, "@p0", "foo%");
    }

    [Fact]
    public void Postgres_LikeEscapesBackslashAndPercentAndUnderscore()
    {
        // Pattern "100%*?" → Literal("100%"), Star, Question → backslash-escaped: "100\%%_"
        var clause = WildcardPattern.ToPredicate("100%*?").ToSql(SqlDialect.Postgres, "filename");
        Assert.Equal(@"filename LIKE @p0 ESCAPE '\'", clause.Sql);
        AssertParam(clause, "@p0", @"100\%%_");
    }

    [Fact]
    public void Postgres_RegexUsesTildeOperator()
    {
        var clause = WildcardPattern.ToPredicate("[abc]*").ToSql(SqlDialect.Postgres, "filename");
        Assert.Equal("filename ~ @p0", clause.Sql);
        Assert.Single(clause.Parameters);
        Assert.StartsWith("^", (string)clause.Parameters[0].Value);
    }

    [Fact]
    public void Postgres_RegexIgnoreCaseUsesTildeStar()
    {
        var clause = WildcardPattern.ToPredicate("[abc]*", ignoreCase: true).ToSql(SqlDialect.Postgres, "filename");
        Assert.Equal("filename ~* @p0", clause.Sql);
    }

    [Fact]
    public void Postgres_AnyOf()
    {
        var clause = WildcardPattern.ToPredicate("{error,warn}: *").ToSql(SqlDialect.Postgres, "msg");
        Assert.Equal(@"(msg LIKE @p0 ESCAPE '\' OR msg LIKE @p1 ESCAPE '\')", clause.Sql);
    }

    // ── Cosmos DB NoSQL ──

    [Fact]
    public void Cosmos_Exact_UsesStringEquals()
    {
        var clause = WildcardPattern.ToPredicate("foo.cs").ToSql(SqlDialect.CosmosSql, "c.name");
        Assert.Equal("STRINGEQUALS(c.name, @p0, false)", clause.Sql);
        AssertParam(clause, "@p0", "foo.cs");
    }

    [Fact]
    public void Cosmos_ExactIgnoreCase_PassesTrueArg()
    {
        var clause = WildcardPattern.ToPredicate("foo.cs", ignoreCase: true).ToSql(SqlDialect.CosmosSql, "c.name");
        Assert.Equal("STRINGEQUALS(c.name, @p0, true)", clause.Sql);
    }

    [Fact]
    public void Cosmos_StartsWith_UsesStartsWithFunction()
    {
        var clause = WildcardPattern.ToPredicate("foo*").ToSql(SqlDialect.CosmosSql, "c.name");
        Assert.Equal("STARTSWITH(c.name, @p0, false)", clause.Sql);
        AssertParam(clause, "@p0", "foo");
    }

    [Fact]
    public void Cosmos_EndsWith_UsesEndsWithFunction()
    {
        var clause = WildcardPattern.ToPredicate("*.cs").ToSql(SqlDialect.CosmosSql, "c.name");
        Assert.Equal("ENDSWITH(c.name, @p0, false)", clause.Sql);
        AssertParam(clause, "@p0", ".cs");
    }

    [Fact]
    public void Cosmos_Contains_UsesContainsFunction()
    {
        var clause = WildcardPattern.ToPredicate("*foo*", ignoreCase: true).ToSql(SqlDialect.CosmosSql, "c.name");
        Assert.Equal("CONTAINS(c.name, @p0, true)", clause.Sql);
        AssertParam(clause, "@p0", "foo");
    }

    [Fact]
    public void Cosmos_StartsAndEndsWith_DecomposesIntoNativeFunctionsWithLengthGuard()
    {
        var clause = WildcardPattern.ToPredicate("foo*bar").ToSql(SqlDialect.CosmosSql, "c.name");
        Assert.Equal(
            "(STARTSWITH(c.name, @p0, false) AND ENDSWITH(c.name, @p1, false) AND LENGTH(c.name) >= 6)",
            clause.Sql);
        AssertParam(clause, "@p0", "foo");
        AssertParam(clause, "@p1", "bar");
    }

    [Fact]
    public void Cosmos_StartsAndEndsWith_LengthGuardPreventsPrefixSuffixOverlap()
    {
        // Pattern "ab*ab": WildcardPattern requires input.Length >= 4, so input "ab"
        // must NOT match. Without the LENGTH guard, STARTSWITH and ENDSWITH would
        // both be true for "ab" and the SQL would diverge from IsMatch.
        var pattern = WildcardPattern.Compile("ab*ab");
        Assert.False(pattern.IsMatch("ab")); // library invariant the SQL must encode

        var clause = pattern.ToPredicate().ToSql(SqlDialect.CosmosSql, "c.name");
        Assert.Equal(
            "(STARTSWITH(c.name, @p0, false) AND ENDSWITH(c.name, @p1, false) AND LENGTH(c.name) >= 4)",
            clause.Sql);
    }

    [Fact]
    public void Cosmos_Like_UsesLikeWithBracketEscape()
    {
        var clause = WildcardPattern.ToPredicate("100%*?").ToSql(SqlDialect.CosmosSql, "c.name");
        Assert.Equal("c.name LIKE @p0", clause.Sql);
        AssertParam(clause, "@p0", "100[%]%_");
    }

    [Fact]
    public void Cosmos_LikeIgnoreCase_LowersBothSides()
    {
        var clause = WildcardPattern.ToPredicate("Foo*Bar?", ignoreCase: true).ToSql(SqlDialect.CosmosSql, "c.name");
        Assert.Equal("LOWER(c.name) LIKE @p0", clause.Sql);
        AssertParam(clause, "@p0", "foo%bar_");
    }

    [Fact]
    public void Cosmos_RegexUsesRegexMatch()
    {
        var clause = WildcardPattern.ToPredicate("[abc]*").ToSql(SqlDialect.CosmosSql, "c.name");
        Assert.Equal("REGEXMATCH(c.name, @p0)", clause.Sql);
    }

    [Fact]
    public void Cosmos_RegexIgnoreCase_PassesIFlag()
    {
        var clause = WildcardPattern.ToPredicate("[abc]*", ignoreCase: true).ToSql(SqlDialect.CosmosSql, "c.name");
        Assert.Equal("REGEXMATCH(c.name, @p0, \"i\")", clause.Sql);
    }

    [Fact]
    public void Cosmos_AnyOf()
    {
        var clause = WildcardPattern.ToPredicate("{error,warn}: *").ToSql(SqlDialect.CosmosSql, "c.msg");
        Assert.Equal(
            "(STARTSWITH(c.msg, @p0, false) OR STARTSWITH(c.msg, @p1, false))",
            clause.Sql);
        AssertParam(clause, "@p0", "error: ");
        AssertParam(clause, "@p1", "warn: ");
    }

    // ── SQLite ──

    [Fact]
    public void Sqlite_Exact_CaseSensitive()
    {
        var clause = WildcardPattern.ToPredicate("foo.cs").ToSql(SqlDialect.Sqlite, "name");
        Assert.Equal("name = @p0", clause.Sql);
        AssertParam(clause, "@p0", "foo.cs");
    }

    [Fact]
    public void Sqlite_Exact_IgnoreCase_UsesCollateNocase()
    {
        var clause = WildcardPattern.ToPredicate("Foo.CS", ignoreCase: true).ToSql(SqlDialect.Sqlite, "name");
        Assert.Equal("name = @p0 COLLATE NOCASE", clause.Sql);
        AssertParam(clause, "@p0", "Foo.CS");
    }

    [Fact]
    public void Sqlite_StartsWith_CaseSensitive_UsesGlob()
    {
        var clause = WildcardPattern.ToPredicate("foo*").ToSql(SqlDialect.Sqlite, "name");
        Assert.Equal("name GLOB @p0", clause.Sql);
        AssertParam(clause, "@p0", "foo*");
    }

    [Fact]
    public void Sqlite_StartsWith_IgnoreCase_UsesLike()
    {
        var clause = WildcardPattern.ToPredicate("foo*", ignoreCase: true).ToSql(SqlDialect.Sqlite, "name");
        Assert.Equal(@"name LIKE @p0 ESCAPE '\'", clause.Sql);
        AssertParam(clause, "@p0", "foo%");
    }

    [Fact]
    public void Sqlite_EndsWith_CaseSensitive_UsesGlob()
    {
        var clause = WildcardPattern.ToPredicate("*.cs").ToSql(SqlDialect.Sqlite, "name");
        Assert.Equal("name GLOB @p0", clause.Sql);
        AssertParam(clause, "@p0", "*.cs");
    }

    [Fact]
    public void Sqlite_Contains_CaseSensitive_UsesGlob()
    {
        var clause = WildcardPattern.ToPredicate("*foo*").ToSql(SqlDialect.Sqlite, "name");
        Assert.Equal("name GLOB @p0", clause.Sql);
        AssertParam(clause, "@p0", "*foo*");
    }

    [Fact]
    public void Sqlite_Contains_IgnoreCase_UsesLike()
    {
        var clause = WildcardPattern.ToPredicate("*foo*", ignoreCase: true).ToSql(SqlDialect.Sqlite, "name");
        Assert.Equal(@"name LIKE @p0 ESCAPE '\'", clause.Sql);
        AssertParam(clause, "@p0", "%foo%");
    }

    [Fact]
    public void Sqlite_StartsAndEndsWith_CaseSensitive_UsesGlob()
    {
        // SQLite GLOB handles overlap correctly without needing a length guard,
        // because '*' must consume zero-or-more chars between the two literals.
        var clause = WildcardPattern.ToPredicate("foo*bar").ToSql(SqlDialect.Sqlite, "name");
        Assert.Equal("name GLOB @p0", clause.Sql);
        AssertParam(clause, "@p0", "foo*bar");
    }

    [Fact]
    public void Sqlite_Like_CaseSensitive_GlobBracketEscapesAsterisks()
    {
        // pattern "100%*?" → Literal("100%"), Star, Question.
        // GLOB renders "100%" as-is (% isn't a GLOB metachar) and uses [*]/[?] for literal *,?.
        // Here the literal contains only '%', not * or ?, so no bracket escape needed.
        var clause = WildcardPattern.ToPredicate("100%*?").ToSql(SqlDialect.Sqlite, "name");
        Assert.Equal("name GLOB @p0", clause.Sql);
        AssertParam(clause, "@p0", "100%*?");
    }

    [Fact]
    public void Sqlite_Like_CaseSensitive_GlobEscapesLiteralStarAndQuestion()
    {
        // We can't construct a wildcard pattern that produces a literal '*' in segments
        // (the wildcard parser interprets it as Star). But we can construct PatternPredicate
        // directly with a Literal containing '*' to verify escape behavior.
        var pred = new PatternPredicate.Like(
            [new LikePart.Literal("a*b?c[d"), LikePart.AnySequence.Instance]);
        var clause = pred.ToSql(SqlDialect.Sqlite, "name");
        Assert.Equal("name GLOB @p0", clause.Sql);
        AssertParam(clause, "@p0", "a[*]b[?]c[[]d*");
    }

    [Fact]
    public void Sqlite_Like_IgnoreCase_LikeEscapesPercentAndUnderscore()
    {
        var clause = WildcardPattern.ToPredicate("100%*?", ignoreCase: true).ToSql(SqlDialect.Sqlite, "name");
        Assert.Equal(@"name LIKE @p0 ESCAPE '\'", clause.Sql);
        AssertParam(clause, "@p0", @"100\%%_");
    }

    [Fact]
    public void Sqlite_Regex_UsesRegexpOperator()
    {
        var clause = WildcardPattern.ToPredicate("[abc]*").ToSql(SqlDialect.Sqlite, "name");
        Assert.Equal("name REGEXP @p0", clause.Sql);
        Assert.Single(clause.Parameters);
        Assert.StartsWith("^", (string)clause.Parameters[0].Value);
    }

    [Fact]
    public void Sqlite_RegexIgnoreCase_PrependsInlineFlag()
    {
        var clause = WildcardPattern.ToPredicate("[abc]*", ignoreCase: true).ToSql(SqlDialect.Sqlite, "name");
        Assert.Equal("name REGEXP @p0", clause.Sql);
        Assert.StartsWith("(?i)", (string)clause.Parameters[0].Value);
    }

    [Fact]
    public void Sqlite_AnyOf_OrsClauses()
    {
        var clause = WildcardPattern.ToPredicate("{error,warn}: *").ToSql(SqlDialect.Sqlite, "msg");
        Assert.Equal("(msg GLOB @p0 OR msg GLOB @p1)", clause.Sql);
        AssertParam(clause, "@p0", "error: *");
        AssertParam(clause, "@p1", "warn: *");
    }

    // ── Cross-cutting ──

    [Fact]
    public void CustomParameterPrefix()
    {
        var clause = WildcardPattern.ToPredicate("foo*").ToSql(SqlDialect.Postgres, "filename", parameterPrefix: "name");
        Assert.Equal(@"filename LIKE @name0 ESCAPE '\'", clause.Sql);
        Assert.Equal("@name0", clause.Parameters[0].Name);
    }

    [Fact]
    public void ParametersAreOrderedByAppearance()
    {
        var clause = WildcardPattern.ToPredicate("{a*,b*,c*}").ToSql(SqlDialect.SqlServer, "x");
        Assert.Equal(3, clause.Parameters.Count);
        Assert.Equal("@p0", clause.Parameters[0].Name);
        Assert.Equal("a%", clause.Parameters[0].Value);
        Assert.Equal("@p1", clause.Parameters[1].Name);
        Assert.Equal("b%", clause.Parameters[1].Value);
        Assert.Equal("@p2", clause.Parameters[2].Name);
        Assert.Equal("c%", clause.Parameters[2].Value);
    }

    [Fact]
    public void NestedAnyOfDoesNotDoubleWrap()
    {
        // Single-alternative AnyOf renders as the inner predicate without parens.
        var single = new PatternPredicate.AnyOf([new PatternPredicate.Exact("x")]);
        var clause = single.ToSql(SqlDialect.SqlServer, "c");
        Assert.Equal("c = @p0", clause.Sql);
    }

    private static void AssertParam(SqlClause clause, string name, object value)
    {
        var match = clause.Parameters.FirstOrDefault(p => p.Name == name);
        Assert.NotEqual(default, match);
        Assert.Equal(value, match.Value);
    }
}

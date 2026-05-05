using System.Linq.Expressions;
using Wildcard;

namespace Wildcard.Tests;

public class PatternPredicateLinqTests
{
    private record Item(string Name);

    private static Func<Item, bool> Compile(string pattern, bool ignoreCase = false) =>
        WildcardPattern.ToPredicate(pattern, ignoreCase)
            .ToExpression<Item>(i => i.Name)
            .Compile();

    // ── Per-predicate runtime behavior ──

    [Theory]
    [InlineData("foo.cs", "foo.cs", true)]
    [InlineData("foo.cs", "Foo.cs", false)]
    [InlineData("foo.cs", "bar.cs", false)]
    public void Exact_CaseSensitive(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, Compile(pattern)(new Item(input)));
    }

    [Theory]
    [InlineData("foo.cs", "FOO.CS", true)]
    [InlineData("foo.cs", "Foo.cs", true)]
    [InlineData("foo.cs", "bar.cs", false)]
    public void Exact_IgnoreCase(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, Compile(pattern, ignoreCase: true)(new Item(input)));
    }

    [Theory]
    [InlineData("foo*", "foobar", true)]
    [InlineData("foo*", "foo", true)]
    [InlineData("foo*", "FOObar", false)]
    [InlineData("foo*", "barfoo", false)]
    public void StartsWith_CaseSensitive(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, Compile(pattern)(new Item(input)));
    }

    [Theory]
    [InlineData("foo*", "FOObar", true)]
    [InlineData("foo*", "Foobar", true)]
    public void StartsWith_IgnoreCase(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, Compile(pattern, ignoreCase: true)(new Item(input)));
    }

    [Theory]
    [InlineData("*.cs", "foo.cs", true)]
    [InlineData("*.cs", "foo.CS", false)]
    [InlineData("*.cs", "foo.txt", false)]
    public void EndsWith_CaseSensitive(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, Compile(pattern)(new Item(input)));
    }

    [Theory]
    [InlineData("*foo*", "abcfoodef", true)]
    [InlineData("*foo*", "abcdef", false)]
    public void Contains_CaseSensitive(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, Compile(pattern)(new Item(input)));
    }

    [Theory]
    [InlineData("foo*bar", "foobar", true)]    // length == 6, boundary
    [InlineData("foo*bar", "foo123bar", true)]
    [InlineData("foo*bar", "foo", false)]      // length < 6
    [InlineData("ab*ab", "ab", false)]         // overlap case — must enforce length guard
    [InlineData("ab*ab", "abab", true)]        // length == 4, boundary
    [InlineData("ab*ab", "abXab", true)]
    public void StartsAndEndsWith_LengthGuardPreventsOverlap(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, Compile(pattern)(new Item(input)));
    }

    [Theory]
    [InlineData("*a*b*c", "axbxc", true)]
    [InlineData("*a*b*c", "abc", true)]
    [InlineData("*a*b*c", "cba", false)]   // ordering matters
    [InlineData("*a*b*c", "ab", false)]
    [InlineData("t?st", "test", true)]
    [InlineData("t?st", "tast", true)]
    [InlineData("t?st", "toast", false)]   // ? matches exactly one char
    [InlineData("???", "abc", true)]
    [InlineData("???", "ab", false)]
    public void Like_GeneralWildcards(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, Compile(pattern)(new Item(input)));
    }

    [Theory]
    [InlineData("[abc]*", "alpha", true)]
    [InlineData("[abc]*", "delta", false)]
    [InlineData("[a-c]*", "beta", true)]
    [InlineData("[a-c]*", "delta", false)]
    public void Regex_FromCharClassPatterns(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, Compile(pattern)(new Item(input)));
    }

    [Theory]
    [InlineData("{error,warn}: *", "error: oops", true)]
    [InlineData("{error,warn}: *", "warn: heads up", true)]
    [InlineData("{error,warn}: *", "info: neither", false)]
    public void AnyOf_BraceAlternation(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, Compile(pattern)(new Item(input)));
    }

    // ── Expression structure (for EF translation predictability) ──

    [Fact]
    public void IgnoreCase_UsesOrdinalIgnoreCaseStringComparison()
    {
        // EF Core providers detect the StringComparison.OrdinalIgnoreCase argument
        // and translate accordingly. Make sure that is what we emit.
        var expr = WildcardPattern.ToPredicate("foo*", ignoreCase: true)
            .ToExpression<Item>(i => i.Name);
        var asString = expr.ToString();
        Assert.Contains("OrdinalIgnoreCase", asString);
        Assert.Contains("StartsWith", asString);
    }

    [Fact]
    public void StartsWith_CaseSensitive_DoesNotUseStringComparisonOverload()
    {
        // The bare StartsWith(string) overload is what EF Core SQL Server / SQLite / Postgres
        // providers translate to LIKE without complaint. Adding a StringComparison argument
        // can break translation on some providers.
        var expr = WildcardPattern.ToPredicate("foo*")
            .ToExpression<Item>(i => i.Name);
        var asString = expr.ToString();
        Assert.DoesNotContain("OrdinalIgnoreCase", asString);
        Assert.DoesNotContain("Ordinal,", asString);
    }

    // ── Library-equivalence sweep ──

    public static IEnumerable<object[]> EquivalencePatterns => new[]
    {
        // pattern, ignoreCase
        new object[] { "foo.cs",       false },
        new object[] { "foo.cs",       true  },
        new object[] { "foo*",         false },
        new object[] { "*.cs",         false },
        new object[] { "*foo*",        false },
        new object[] { "foo*bar",      false },
        new object[] { "ab*ab",        false }, // overlap
        new object[] { "t?st",         false },
        new object[] { "*a*b*c",       false },
        new object[] { "100%*?",       false }, // literal % and _
        new object[] { "[abc]*",       false }, // regex path
        new object[] { "[a-z]+",       false }, // wildcard syntax doesn't parse + as quantifier — '+' is literal
        new object[] { "{error,warn}: *", false },
        new object[] { "{a*,b*,c*}",   false },
        new object[] { "*",            false },
        new object[] { "???",          false },
    };

    public static IEnumerable<string> SweepInputs => new[]
    {
        "", "a", "ab", "abc", "foo.cs", "Foo.cs", "FOO.CS",
        "foobar", "foo123bar", "barfoo", "abxbxc", "abc123",
        "error: hi", "warn: yo", "info: nope",
        "100%abc_x", "test", "toast", "aXa", "abab", "ababab",
        "alpha", "delta", "beta", "z",
    };

    [Theory]
    [MemberData(nameof(EquivalencePatterns))]
    public void Compiled_Lambda_AgreesWithIsMatch_AcrossManyInputs(string pattern, bool ignoreCase)
    {
        var compiled = WildcardPattern.Compile(pattern, ignoreCase);
        var fn = compiled.ToPredicate().ToExpression<Item>(i => i.Name).Compile();
        foreach (var input in SweepInputs)
        {
            var expected = compiled.IsMatch(input);
            var actual = fn(new Item(input));
            Assert.True(
                expected == actual,
                $"Divergence: pattern='{pattern}' ic={ignoreCase} input='{input}' lib={expected} expr={actual}");
        }
    }

    // ── Misc ──

    [Fact]
    public void Works_With_DeepPropertyAccessor()
    {
        // Property accessor doesn't have to be a single member access — any expression
        // returning string works (e.g. nested property, computed expression).
        var pred = WildcardPattern.ToPredicate("foo*");
        var expr = pred.ToExpression<Wrapper>(w => w.Inner.Name);
        var fn = expr.Compile();
        Assert.True(fn(new Wrapper(new Item("foobar"))));
        Assert.False(fn(new Wrapper(new Item("barfoo"))));
    }

    private record Wrapper(Item Inner);

    [Fact]
    public void Throws_OnNullPredicate()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ((PatternPredicate)null!).ToExpression<Item>(i => i.Name));
    }

    [Fact]
    public void Throws_OnNullAccessor()
    {
        Assert.Throws<ArgumentNullException>(() =>
            WildcardPattern.ToPredicate("foo").ToExpression<Item>(null!));
    }
}

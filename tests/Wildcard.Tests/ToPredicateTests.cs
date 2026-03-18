using Wildcard;

namespace Wildcard.Tests;

public class ToPredicateTests
{
    [Theory]
    [InlineData("test")]
    [InlineData("hello world")]
    [InlineData("")]
    public void PureLiteral_ReturnsExact(string pattern)
    {
        var pred = WildcardPattern.Compile(pattern).ToPredicate();
        var exact = Assert.IsType<PatternPredicate.Exact>(pred);
        Assert.Equal(pattern, exact.Value);
        Assert.False(exact.IgnoreCase);
    }

    [Theory]
    [InlineData("test*", "test")]
    [InlineData("hello*", "hello")]
    [InlineData("a*", "a")]
    public void PrefixStar_ReturnsStartsWith(string pattern, string expectedPrefix)
    {
        var pred = WildcardPattern.Compile(pattern).ToPredicate();
        var sw = Assert.IsType<PatternPredicate.StartsWith>(pred);
        Assert.Equal(expectedPrefix, sw.Prefix);
    }

    [Theory]
    [InlineData("*test", "test")]
    [InlineData("*.csv", ".csv")]
    [InlineData("*x", "x")]
    public void StarSuffix_ReturnsEndsWith(string pattern, string expectedSuffix)
    {
        var pred = WildcardPattern.Compile(pattern).ToPredicate();
        var ew = Assert.IsType<PatternPredicate.EndsWith>(pred);
        Assert.Equal(expectedSuffix, ew.Suffix);
    }

    [Theory]
    [InlineData("*test*", "test")]
    [InlineData("*hello*", "hello")]
    public void StarContainsStar_ReturnsContains(string pattern, string expectedValue)
    {
        var pred = WildcardPattern.Compile(pattern).ToPredicate();
        var c = Assert.IsType<PatternPredicate.Contains>(pred);
        Assert.Equal(expectedValue, c.Value);
    }

    [Theory]
    [InlineData("pre*suf", "pre", "suf")]
    [InlineData("hello*world", "hello", "world")]
    public void PrefixStarSuffix_ReturnsStartsAndEndsWith(string pattern, string expectedPrefix, string expectedSuffix)
    {
        var pred = WildcardPattern.Compile(pattern).ToPredicate();
        var se = Assert.IsType<PatternPredicate.StartsAndEndsWith>(pred);
        Assert.Equal(expectedPrefix, se.Prefix);
        Assert.Equal(expectedSuffix, se.Suffix);
    }

    [Theory]
    [InlineData("t?st")]
    [InlineData("[abc]*")]
    [InlineData("*a*b*")]
    [InlineData("report_[0-9]*.csv")]
    public void General_ReturnsRegex(string pattern)
    {
        var pred = WildcardPattern.Compile(pattern).ToPredicate();
        var regex = Assert.IsType<PatternPredicate.Regex>(pred);
        Assert.StartsWith("^", regex.Pattern);
        Assert.EndsWith("$", regex.Pattern);
    }

    [Fact]
    public void IgnoreCase_Propagates()
    {
        var pred = WildcardPattern.Compile("test*", ignoreCase: true).ToPredicate();
        var sw = Assert.IsType<PatternPredicate.StartsWith>(pred);
        Assert.True(sw.IgnoreCase);
    }

    [Fact]
    public void IgnoreCase_PropagatesForRegex()
    {
        var pred = WildcardPattern.Compile("t?st", ignoreCase: true).ToPredicate();
        var regex = Assert.IsType<PatternPredicate.Regex>(pred);
        Assert.True(regex.IgnoreCase);
    }

    [Fact]
    public void StaticConvenience_Works()
    {
        var pred = WildcardPattern.ToPredicate("test*");
        Assert.IsType<PatternPredicate.StartsWith>(pred);
    }

    [Fact]
    public void SingleStar_ReturnsRegex()
    {
        var pred = WildcardPattern.Compile("*").ToPredicate();
        var regex = Assert.IsType<PatternPredicate.Regex>(pred);
        Assert.Equal("^.*$", regex.Pattern);
    }

    // ── Brace alternation → AnyOf ──

    [Fact]
    public void BraceAlternation_ReturnsAnyOf()
    {
        var pred = WildcardPattern.Compile("{error,warn}: *").ToPredicate();
        var anyOf = Assert.IsType<PatternPredicate.AnyOf>(pred);
        Assert.Equal(2, anyOf.Alternatives.Length);
        var a0 = Assert.IsType<PatternPredicate.StartsWith>(anyOf.Alternatives[0]);
        Assert.Equal("error: ", a0.Prefix);
        var a1 = Assert.IsType<PatternPredicate.StartsWith>(anyOf.Alternatives[1]);
        Assert.Equal("warn: ", a1.Prefix);
    }

    [Fact]
    public void BraceAlternation_MixedShapes()
    {
        var pred = WildcardPattern.Compile("{exact,prefix*,*suffix}").ToPredicate();
        var anyOf = Assert.IsType<PatternPredicate.AnyOf>(pred);
        Assert.Equal(3, anyOf.Alternatives.Length);
        Assert.IsType<PatternPredicate.Exact>(anyOf.Alternatives[0]);
        Assert.IsType<PatternPredicate.StartsWith>(anyOf.Alternatives[1]);
        Assert.IsType<PatternPredicate.EndsWith>(anyOf.Alternatives[2]);
    }

    [Fact]
    public void BraceAlternation_IgnoreCase_Propagates()
    {
        var pred = WildcardPattern.Compile("{a,b}", ignoreCase: true).ToPredicate();
        var anyOf = Assert.IsType<PatternPredicate.AnyOf>(pred);
        Assert.True(anyOf.IgnoreCase);
        Assert.All(anyOf.Alternatives, a => Assert.True(a.IgnoreCase));
    }
}

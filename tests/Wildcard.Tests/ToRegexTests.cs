using System.Text.RegularExpressions;
using Wildcard;

namespace Wildcard.Tests;

public class ToRegexTests
{
    [Theory]
    [InlineData("*", "^.*$")]
    [InlineData("***", "^.*$")]
    [InlineData("*.csv", @"^.*\.csv$")]
    [InlineData("hello*", "^hello.*$")]
    [InlineData("*world", "^.*world$")]
    [InlineData("h*o*d", "^h.*o.*d$")]
    public void Star_ConvertsCorrectly(string pattern, string expectedRegex)
    {
        var regex = WildcardPattern.Compile(pattern).ToRegex();
        Assert.Equal(expectedRegex, regex.ToString());
    }

    [Theory]
    [InlineData("?", "^.$")]
    [InlineData("???", "^...$")]
    [InlineData("h?llo", "^h.llo$")]
    public void QuestionMark_ConvertsCorrectly(string pattern, string expectedRegex)
    {
        var regex = WildcardPattern.Compile(pattern).ToRegex();
        Assert.Equal(expectedRegex, regex.ToString());
    }

    [Theory]
    [InlineData("[abc]", "^[abc]$")]
    [InlineData("[a-z]", "^[a-z]$")]
    [InlineData("[a-zA-Z]", "^[a-zA-Z]$")]
    [InlineData("[!abc]", "^[^abc]$")]
    [InlineData("[^0-9]", "^[^0-9]$")]
    public void CharClass_ConvertsCorrectly(string pattern, string expectedRegex)
    {
        var regex = WildcardPattern.Compile(pattern).ToRegex();
        Assert.Equal(expectedRegex, regex.ToString());
    }

    [Theory]
    [InlineData("hello\\*", @"^hello\*$")]
    [InlineData("hello\\?", @"^hello\?$")]
    [InlineData("test\\[1\\]", @"^test\[1]$")]
    public void Escaped_ConvertsCorrectly(string pattern, string expectedRegex)
    {
        var regex = WildcardPattern.Compile(pattern).ToRegex();
        Assert.Equal(expectedRegex, regex.ToString());
    }

    [Theory]
    [InlineData("file.txt", @"^file\.txt$")]
    [InlineData("a+b", @"^a\+b$")]
    [InlineData("(foo)", @"^\(foo\)$")]
    [InlineData("a|b", @"^a\|b$")]
    [InlineData("price$10", @"^price\$10$")]
    public void RegexMetaChars_AreEscaped(string pattern, string expectedRegex)
    {
        var regex = WildcardPattern.Compile(pattern).ToRegex();
        Assert.Equal(expectedRegex, regex.ToString());
    }

    [Fact]
    public void CaseInsensitive_SetsRegexOption()
    {
        var regex = WildcardPattern.Compile("hello", ignoreCase: true).ToRegex();
        Assert.Equal("^hello$", regex.ToString());
        Assert.True((regex.Options & RegexOptions.IgnoreCase) != 0);
    }

    [Fact]
    public void CaseSensitive_NoRegexOption()
    {
        var regex = WildcardPattern.Compile("hello").ToRegex();
        Assert.True((regex.Options & RegexOptions.IgnoreCase) == 0);
    }

    [Fact]
    public void ComplexPattern_ProducesCorrectRegex()
    {
        var regex = WildcardPattern.Compile("report_[0-9][0-9][0-9][0-9]*.csv").ToRegex();
        Assert.Equal(@"^report_[0-9][0-9][0-9][0-9].*\.csv$", regex.ToString());
    }

    [Fact]
    public void EmptyPattern_ProducesAnchors()
    {
        var regex = WildcardPattern.Compile("").ToRegex();
        Assert.Equal("^$", regex.ToString());
    }

    [Theory]
    [InlineData("*.[ch]pp", @"^.*\.[ch]pp$")]
    [InlineData("src/*/test?.cs", @"^src/.*/test.\.cs$")]
    public void Combined_ProducesCorrectRegex(string pattern, string expectedRegex)
    {
        var regex = WildcardPattern.Compile(pattern).ToRegex();
        Assert.Equal(expectedRegex, regex.ToString());
    }

[Fact]
    public void EscapedCharInCharClass_CompilesAndMatches()
    {
        // Pattern [\]] should match a literal ]
        var p = WildcardPattern.Compile("[\\]]");
        Assert.True(p.IsMatch("]"));
        Assert.False(p.IsMatch("a"));

        var regex = p.ToRegex();
        Assert.Matches(regex, "]");
    }

    [Fact]
    public void EscapedRangeEndInCharClass_CompilesAndMatches()
    {
        // Pattern [Z-\]] should match chars in range Z (90) to ] (93)
        var p = WildcardPattern.Compile("[Z-\\]]");
        Assert.True(p.IsMatch("]"));
        Assert.True(p.IsMatch("Z"));
        Assert.True(p.IsMatch("["));  // ASCII 91, between Z and ]
        Assert.False(p.IsMatch("a"));

        var regex = p.ToRegex();
        Assert.Matches(regex, "]");
        Assert.Matches(regex, "Z");
    }

    [Fact]
    public void EscapedCharInsideBracket_ToRegex()
    {
        // Escaped special char inside bracket expression in ToRegex conversion
        var p = WildcardPattern.Compile("[\\*]");
        var regex = p.ToRegex();
        Assert.Matches(regex, "*");
        Assert.DoesNotMatch(regex, "a");
    }

    // ── Brace alternation ──

    [Fact]
    public void Brace_SimpleAlternation()
    {
        var regex = WildcardPattern.Compile("{a,b}").ToRegex();
        Assert.Equal("^(a|b)$", regex.ToString());
    }

    [Fact]
    public void Brace_SuffixAlternation()
    {
        var regex = WildcardPattern.Compile("*.{cs,fs}").ToRegex();
        Assert.Equal(@"^(.*\.cs|.*\.fs)$", regex.ToString());
    }

    [Fact]
    public void Brace_ThreeAlternatives()
    {
        var regex = WildcardPattern.Compile("{a,b,c}").ToRegex();
        Assert.Equal("^(a|b|c)$", regex.ToString());
    }

    [Fact]
    public void Brace_CaseInsensitive_SetsOption()
    {
        var regex = WildcardPattern.Compile("{hello,world}", ignoreCase: true).ToRegex();
        Assert.True((regex.Options & RegexOptions.IgnoreCase) != 0);
        Assert.Equal("^(hello|world)$", regex.ToString());
    }

    [Fact]
    public void Brace_RegexActuallyMatches()
    {
        var regex = WildcardPattern.Compile("*.{cs,fs}").ToRegex();
        Assert.Matches(regex, "file.cs");
        Assert.Matches(regex, "file.fs");
        Assert.DoesNotMatch(regex, "file.vb");
    }
}

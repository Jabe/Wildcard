using Wildcard;

namespace Wildcard.Tests;

public class WildcardPatternTests
{
    // ── Star (*) ──

    [Theory]
    [InlineData("*", "")]
    [InlineData("*", "anything")]
    [InlineData("*", "hello world")]
    public void Star_MatchesAnything(string pattern, string input) =>
        Assert.True(WildcardPattern.IsMatch(pattern, input));

    [Theory]
    [InlineData("hello*", "hello")]
    [InlineData("hello*", "hello world")]
    [InlineData("*world", "hello world")]
    [InlineData("*world", "world")]
    [InlineData("h*d", "hd")]
    [InlineData("h*d", "hello world")]
    [InlineData("h*o*d", "hello world")]
    public void Star_MatchesSequences(string pattern, string input) =>
        Assert.True(WildcardPattern.IsMatch(pattern, input));

    [Theory]
    [InlineData("hello*", "hell")]
    [InlineData("*world", "worl")]
    [InlineData("h*d", "hello world!")]
    public void Star_Rejects(string pattern, string input) =>
        Assert.False(WildcardPattern.IsMatch(pattern, input));

    // ── Question mark (?) ──

    [Theory]
    [InlineData("?", "a")]
    [InlineData("?", "Z")]
    [InlineData("??", "ab")]
    [InlineData("h?llo", "hello")]
    [InlineData("h?llo", "hallo")]
    public void QuestionMark_MatchesSingleChar(string pattern, string input) =>
        Assert.True(WildcardPattern.IsMatch(pattern, input));

    [Theory]
    [InlineData("?", "")]
    [InlineData("?", "ab")]
    [InlineData("??", "a")]
    [InlineData("h?llo", "hllo")]
    [InlineData("h?llo", "heello")]
    public void QuestionMark_Rejects(string pattern, string input) =>
        Assert.False(WildcardPattern.IsMatch(pattern, input));

    // ── Character classes ──

    [Theory]
    [InlineData("[abc]", "a")]
    [InlineData("[abc]", "b")]
    [InlineData("[abc]", "c")]
    [InlineData("[a-z]", "m")]
    [InlineData("[0-9]", "5")]
    [InlineData("[a-zA-Z]", "Z")]
    [InlineData("[a-zA-Z]", "a")]
    public void CharClass_Matches(string pattern, string input) =>
        Assert.True(WildcardPattern.IsMatch(pattern, input));

    [Theory]
    [InlineData("[abc]", "d")]
    [InlineData("[a-z]", "A")]
    [InlineData("[0-9]", "a")]
    [InlineData("[abc]", "")]
    [InlineData("[abc]", "ab")]
    public void CharClass_Rejects(string pattern, string input) =>
        Assert.False(WildcardPattern.IsMatch(pattern, input));

    // ── Negated character classes ──

    [Theory]
    [InlineData("[!abc]", "d")]
    [InlineData("[!abc]", "z")]
    [InlineData("[!0-9]", "a")]
    [InlineData("[^abc]", "x")]
    public void NegatedCharClass_Matches(string pattern, string input) =>
        Assert.True(WildcardPattern.IsMatch(pattern, input));

    [Theory]
    [InlineData("[!abc]", "a")]
    [InlineData("[!abc]", "b")]
    [InlineData("[!0-9]", "5")]
    public void NegatedCharClass_Rejects(string pattern, string input) =>
        Assert.False(WildcardPattern.IsMatch(pattern, input));

    // ── Combined patterns ──

    [Theory]
    [InlineData("*.txt", "readme.txt")]
    [InlineData("*.txt", ".txt")]
    [InlineData("file?.log", "file1.log")]
    [InlineData("file?.log", "fileA.log")]
    [InlineData("[hH]ello*", "Hello World")]
    [InlineData("[hH]ello*", "hello")]
    [InlineData("*.[ch]pp", "main.cpp")]
    [InlineData("*.[ch]pp", "header.hpp")]
    [InlineData("src/*/test?.cs", "src/foo/test1.cs")]
    [InlineData("data_[0-9][0-9][0-9].csv", "data_042.csv")]
    public void Combined_Matches(string pattern, string input) =>
        Assert.True(WildcardPattern.IsMatch(pattern, input));

    [Theory]
    [InlineData("*.txt", "readme.csv")]
    [InlineData("file?.log", "file12.log")]
    [InlineData("[hH]ello*", "jello")]
    [InlineData("*.[ch]pp", "main.py")]
    public void Combined_Rejects(string pattern, string input) =>
        Assert.False(WildcardPattern.IsMatch(pattern, input));

    // ── Escape sequences ──

    [Theory]
    [InlineData("hello\\*", "hello*")]
    [InlineData("hello\\?", "hello?")]
    [InlineData("test\\[1\\]", "test[1]")]
    public void Escape_Matches(string pattern, string input) =>
        Assert.True(WildcardPattern.IsMatch(pattern, input));

    [Theory]
    [InlineData("hello\\*", "helloX")]
    [InlineData("hello\\?", "helloX")]
    public void Escape_Rejects(string pattern, string input) =>
        Assert.False(WildcardPattern.IsMatch(pattern, input));

    // ── Case insensitive ──

    [Theory]
    [InlineData("hello", "HELLO")]
    [InlineData("Hello", "hello")]
    [InlineData("h*O", "HELLO")]
    [InlineData("[a-z]", "A")]
    public void CaseInsensitive_Matches(string pattern, string input) =>
        Assert.True(WildcardPattern.IsMatch(pattern, input, ignoreCase: true));

    [Theory]
    [InlineData("hello", "HELLO")]
    [InlineData("[a-z]", "A")]
    public void CaseSensitive_Rejects(string pattern, string input) =>
        Assert.False(WildcardPattern.IsMatch(pattern, input, ignoreCase: false));

    // ── Edge cases ──

    [Fact]
    public void EmptyPattern_MatchesEmpty() =>
        Assert.True(WildcardPattern.IsMatch("", ""));

    [Fact]
    public void EmptyPattern_RejectsNonEmpty() =>
        Assert.False(WildcardPattern.IsMatch("", "a"));

    [Fact]
    public void LiteralPattern_ExactMatch() =>
        Assert.True(WildcardPattern.IsMatch("hello", "hello"));

    [Fact]
    public void LiteralPattern_RejectsMismatch() =>
        Assert.False(WildcardPattern.IsMatch("hello", "world"));

    [Fact]
    public void ConsecutiveStars_CollapsedToOne()
    {
        Assert.True(WildcardPattern.IsMatch("***", "anything"));
        Assert.True(WildcardPattern.IsMatch("a***b", "axyzb"));
    }

    [Fact]
    public void OnlyQuestions_MatchesExactLength()
    {
        Assert.True(WildcardPattern.IsMatch("???", "abc"));
        Assert.False(WildcardPattern.IsMatch("???", "ab"));
        Assert.False(WildcardPattern.IsMatch("???", "abcd"));
    }

    [Fact]
    public void Compile_ReusablePattern()
    {
        var p = WildcardPattern.Compile("*.cs");
        Assert.True(p.IsMatch("file.cs"));
        Assert.True(p.IsMatch("Program.cs"));
        Assert.False(p.IsMatch("file.txt"));
    }

    [Fact]
    public void ToString_ReturnsOriginalPattern()
    {
        var p = WildcardPattern.Compile("hello*world");
        Assert.Equal("hello*world", p.ToString());
    }

    [Fact]
    public void NullPattern_Throws() =>
        Assert.Throws<ArgumentNullException>(() => WildcardPattern.Compile(null!));

    [Fact]
    public void NullInput_Throws()
    {
        var p = WildcardPattern.Compile("*");
        Assert.Throws<ArgumentNullException>(() => p.IsMatch((string)null!));
    }

    // ── Stress / pathological ──

    [Fact]
    public void StressTest_ManyStars()
    {
        // Pattern: *a*b*c*d*e* on a long string
        var p = WildcardPattern.Compile("*a*b*c*d*e*");
        Assert.True(p.IsMatch("xxa xxb xxc xxd xxe xx"));
        Assert.True(p.IsMatch("abcde"));
        Assert.False(p.IsMatch("abcd"));
    }

    [Fact]
    public void StressTest_LongInput()
    {
        var longStr = new string('a', 10_000) + "b";
        var p = WildcardPattern.Compile("*b");
        Assert.True(p.IsMatch(longStr));

        var p2 = WildcardPattern.Compile("*c");
        Assert.False(p2.IsMatch(longStr));
    }

    [Fact]
    public void StressTest_Pathological()
    {
        // a]?]?]?]?...a should not explode (no exponential backtracking)
        var input = new string('a', 25);
        var pattern = string.Concat(Enumerable.Repeat("?", 25));
        Assert.True(WildcardPattern.IsMatch(pattern, input));

        // Worst case for naive backtracking: *a*a*a...a (no trailing star) on "bbb...b"
        var p = string.Concat(Enumerable.Repeat("*a", 10));
        var inp = new string('b', 20);
        Assert.False(WildcardPattern.IsMatch(p, inp));
    }

    // ── Star+Literal IndexOf optimization ──

    [Fact]
    public void StarLiteral_LongInput_Match()
    {
        var p = WildcardPattern.Compile("*.csv");
        Assert.True(p.IsMatch(new string('x', 50_000) + ".csv"));
    }

    [Fact]
    public void StarLiteral_LongInput_NoMatch_EarlyReturn()
    {
        var p = WildcardPattern.Compile("*.csv");
        Assert.False(p.IsMatch(new string('x', 50_000) + ".txt"));
    }

    [Fact]
    public void StarLiteral_MultiStar_LogLine()
    {
        Assert.True(WildcardPattern.IsMatch("*ERROR*timeout*",
            "[2024-03-15 14:22:01] ERROR   Payment service timeout after 30s"));
        Assert.False(WildcardPattern.IsMatch("*ERROR*timeout*",
            "[2024-03-15 14:22:01] INFO    User login successful: user_id=4821"));
    }

    [Fact]
    public void StarLiteral_CaseInsensitive()
    {
        var p = WildcardPattern.Compile("*error*", ignoreCase: true);
        Assert.True(p.IsMatch("prefix ERROR suffix"));
        Assert.False(p.IsMatch("no match here"));
    }

    [Fact]
    public void StarLiteral_Backtrack_SecondOccurrenceMatches()
    {
        // First "foo" leads to failure; must backtrack to second "foo"
        Assert.True(WildcardPattern.IsMatch("*foo*bar", "fooXXfoobar"));
        Assert.False(WildcardPattern.IsMatch("*foo*bar", "fooXXfoobaz"));
    }

    [Fact]
    public void StarLiteral_SingleStar_SecondOccurrenceIsTheMatch()
    {
        // *foo must match the LAST possible foo to consume all input
        Assert.True(WildcardPattern.IsMatch("*foo", "fooXfoo"));
    }

    // ── Round 2 optimization tests ──

    [Fact]
    public void TrailingStar_ReturnsImmediately()
    {
        var p = WildcardPattern.Compile("foo*");
        Assert.True(p.IsMatch("foo"));
        Assert.True(p.IsMatch("foo" + new string('x', 50_000)));
        Assert.False(p.IsMatch("bar"));
    }

    [Fact]
    public void SingleCharClass_PromotedToLiteral()
    {
        Assert.True(WildcardPattern.IsMatch("[[]hello", "[hello"));
        Assert.False(WildcardPattern.IsMatch("[[]hello", "xhello"));
        Assert.True(WildcardPattern.IsMatch("[[]2024-03-15]*", "[2024-03-15] foo"));
    }

    [Fact]
    public void EndsWith_TerminalStarLiteral()
    {
        var p = WildcardPattern.Compile("*.csv");
        Assert.True(p.IsMatch("file.csv"));
        Assert.False(p.IsMatch("file.csv.bak"));
        Assert.True(WildcardPattern.IsMatch("report*.csv", "report_2024.csv"));
    }

    // ── Round 3 optimization tests ──

    [Fact]
    public void QuestionRun_MatchesExactCount()
    {
        Assert.True(WildcardPattern.IsMatch("a????z", "abcxyz"));
        Assert.False(WildcardPattern.IsMatch("a????z", "abcz"));
        Assert.False(WildcardPattern.IsMatch("a????z", "abcdefz"));
    }

    [Fact]
    public void QuestionRun_WithStar()
    {
        Assert.True(WildcardPattern.IsMatch("???*", "abc"));
        Assert.True(WildcardPattern.IsMatch("???*", "abcdef"));
        Assert.False(WildcardPattern.IsMatch("???*", "ab"));
    }

    [Fact]
    public void CaseInsensitive_CharClass_SearchValues()
    {
        var p = WildcardPattern.Compile("[aeiou]*", ignoreCase: true);
        Assert.True(p.IsMatch("Apple"));
        Assert.True(p.IsMatch("orange"));
        Assert.False(p.IsMatch("Banana"));
    }

    [Fact]
    public void Shape_PureLiteral()
    {
        var p = WildcardPattern.Compile("hello");
        Assert.True(p.IsMatch("hello"));
        Assert.False(p.IsMatch("hello!"));
        Assert.False(p.IsMatch("hell"));
    }

    [Fact]
    public void Shape_PrefixStarSuffix()
    {
        var p = WildcardPattern.Compile("report*.csv");
        Assert.True(p.IsMatch("report_2024.csv"));
        Assert.True(p.IsMatch("report.csv"));
        Assert.False(p.IsMatch("report.txt"));
        Assert.False(p.IsMatch("data.csv"));
    }

    [Fact]
    public void Shape_StarContainsStar()
    {
        var p = WildcardPattern.Compile("*ERROR*");
        Assert.True(p.IsMatch("some ERROR here"));
        Assert.True(p.IsMatch("ERROR"));
        Assert.False(p.IsMatch("no match"));
    }

    [Fact]
    public void Shape_PrefixStarSuffix_Overlap()
    {
        var p = WildcardPattern.Compile("abc*bc");
        Assert.True(p.IsMatch("abcbc"));
        Assert.True(p.IsMatch("abcXbc"));
        Assert.False(p.IsMatch("abcb"));
    }
}

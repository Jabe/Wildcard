using Wildcard;

namespace Wildcard.Tests;

public class TryMatchTests
{
    [Fact]
    public void StarSuffix_CapturesPrefix()
    {
        var p = WildcardPattern.Compile("*.csv");
        Assert.True(p.TryMatch("report.csv", out var captures));
        Assert.Equal(["report"], captures);
    }

    [Fact]
    public void PrefixStar_CapturesSuffix()
    {
        var p = WildcardPattern.Compile("hello*");
        Assert.True(p.TryMatch("hello world", out var captures));
        Assert.Equal([" world"], captures);
    }

    [Fact]
    public void PrefixStarSuffix_CapturesMiddle()
    {
        var p = WildcardPattern.Compile("report*.csv");
        Assert.True(p.TryMatch("report_2024.csv", out var captures));
        Assert.Equal(["_2024"], captures);
    }

    [Fact]
    public void StarContainsStar_CapturesBoth()
    {
        var p = WildcardPattern.Compile("*ERROR*");
        Assert.True(p.TryMatch("some ERROR here", out var captures));
        Assert.Equal(["some ", " here"], captures);
    }

    [Fact]
    public void MultipleStars_CapturesAll()
    {
        var p = WildcardPattern.Compile("*-*-*");
        Assert.True(p.TryMatch("a-b-c", out var captures));
        Assert.Equal(["a", "b", "c"], captures);
    }

    [Fact]
    public void StarMatchesEmpty()
    {
        var p = WildcardPattern.Compile("*.csv");
        Assert.True(p.TryMatch(".csv", out var captures));
        Assert.Equal([""], captures);
    }

    [Fact]
    public void NoStars_EmptyCaptures()
    {
        var p = WildcardPattern.Compile("hello");
        Assert.True(p.TryMatch("hello", out var captures));
        Assert.Empty(captures);
    }

    [Fact]
    public void NoMatch_ReturnsFalse()
    {
        var p = WildcardPattern.Compile("*.csv");
        Assert.False(p.TryMatch("report.txt", out var captures));
        Assert.Empty(captures);
    }

    [Fact]
    public void QuestionMark_NotCaptured()
    {
        var p = WildcardPattern.Compile("file?.log");
        Assert.True(p.TryMatch("file1.log", out var captures));
        Assert.Empty(captures);
    }

    [Fact]
    public void StarWithQuestionMark()
    {
        var p = WildcardPattern.Compile("*_?.csv");
        Assert.True(p.TryMatch("report_A.csv", out var captures));
        Assert.Equal(["report"], captures);
    }

    [Fact]
    public void CaseInsensitive_Captures()
    {
        var p = WildcardPattern.Compile("*ERROR*", ignoreCase: true);
        Assert.True(p.TryMatch("some error here", out var captures));
        Assert.Equal(["some ", " here"], captures);
    }

    [Fact]
    public void ConsecutiveStars_SingleCapture()
    {
        var p = WildcardPattern.Compile("a***b");
        Assert.True(p.TryMatch("aXYZb", out var captures));
        Assert.Equal(["XYZ"], captures);
    }

    [Fact]
    public void StarOnly_CapturesEverything()
    {
        var p = WildcardPattern.Compile("*");
        Assert.True(p.TryMatch("anything", out var captures));
        Assert.Equal(["anything"], captures);
    }

    [Fact]
    public void ComplexPattern_LogLine()
    {
        var p = WildcardPattern.Compile("\\[*\\] * - *");
        Assert.True(p.TryMatch("[2024-03-15] ERROR - timeout", out var captures));
        Assert.Equal(["2024-03-15", "ERROR", "timeout"], captures);
    }

[Fact]
    public void PureLiteral_TryMatch_Failure()
    {
        var p = WildcardPattern.Compile("exact");
        Assert.False(p.TryMatch("wrong", out var c));
        Assert.Empty(c);
    }

    [Fact]
    public void PrefixStar_TryMatch_Failure()
    {
        var p = WildcardPattern.Compile("hello*");
        Assert.False(p.TryMatch("goodbye", out var c));
        Assert.Empty(c);
    }

    [Fact]
    public void PrefixStarSuffix_InputTooShort()
    {
        var p = WildcardPattern.Compile("abc*xyz");
        Assert.False(p.TryMatch("ab", out var c));
        Assert.Empty(c);
    }

    [Fact]
    public void PrefixStarSuffix_PrefixDoesNotMatch()
    {
        var p = WildcardPattern.Compile("abc*xyz");
        Assert.False(p.TryMatch("XXXtestxyz", out var c));
        Assert.Empty(c);
    }

    [Fact]
    public void PrefixStarSuffix_SuffixDoesNotMatch()
    {
        var p = WildcardPattern.Compile("abc*xyz");
        Assert.False(p.TryMatch("abctestXXX", out var c));
        Assert.Empty(c);
    }

    [Fact]
    public void StarContainsStar_Failure()
    {
        var p = WildcardPattern.Compile("*needle*");
        Assert.False(p.TryMatch("no match here", out var c));
        Assert.Empty(c);
    }

    [Fact]
    public void Star_QuestionMark_WithCaptures()
    {
        var p = WildcardPattern.Compile("*?x");
        Assert.True(p.TryMatch("abcx", out var c));
        // star captures "ab", ? matches "c"
    }

    [Fact]
    public void Star_QuestionRun_WithCaptures()
    {
        var p = WildcardPattern.Compile("*???end");
        Assert.True(p.TryMatch("startABCend", out var c));
    }

    [Fact]
    public void Star_CharClass_WithCaptures()
    {
        var p = WildcardPattern.Compile("*[abc]end");
        Assert.True(p.TryMatch("prefixaend", out var c));
    }

    [Fact]
    public void ConsecutiveStars_WithCaptures()
    {
        var p = WildcardPattern.Compile("**hello");
        Assert.True(p.TryMatch("XXhello", out var c));
    }

    [Fact]
    public void Star_Literal_EndsWith_WithCaptures()
    {
        var p = WildcardPattern.Compile("start*end");
        Assert.True(p.TryMatch("startmiddleend", out var c));
        Assert.Equal(["middle"], c);
    }

    [Fact]
    public void Backtracking_WithCaptures()
    {
        var p = WildcardPattern.Compile("*a*b");
        Assert.True(p.TryMatch("xaxayb", out var c));
    }

    [Fact]
    public void CloseActiveStar_AtEnd()
    {
        var p = WildcardPattern.Compile("x*");
        Assert.True(p.TryMatch("xhello", out var c));
        Assert.Equal(["hello"], c);
    }

    [Fact]
    public void CaseInsensitive_PureLiteral_Failure()
    {
        var p = WildcardPattern.Compile("HELLO", ignoreCase: true);
        Assert.False(p.TryMatch("world", out var c));
        Assert.Empty(c);
    }

    [Fact]
    public void CaseInsensitive_PrefixStar_Failure()
    {
        var p = WildcardPattern.Compile("HELLO*", ignoreCase: true);
        Assert.False(p.TryMatch("world", out var c));
        Assert.Empty(c);
    }

    [Fact]
    public void CaseInsensitive_StarSuffix_Failure()
    {
        var p = WildcardPattern.Compile("*HELLO", ignoreCase: true);
        Assert.False(p.TryMatch("world", out var c));
        Assert.Empty(c);
    }

    [Fact]
    public void CaseInsensitive_PrefixStarSuffix()
    {
        var p = WildcardPattern.Compile("HE*LO", ignoreCase: true);
        Assert.False(p.TryMatch("world", out var c));
        Assert.True(p.TryMatch("heLLo", out c));
    }

    [Fact]
    public void CaseInsensitive_StarContainsStar_Failure()
    {
        var p = WildcardPattern.Compile("*NEEDLE*", ignoreCase: true);
        Assert.False(p.TryMatch("no match", out var c));
        Assert.Empty(c);
    }

    [Fact]
    public void MatchLiteral_MultiChar_IgnoreCase()
    {
        var p = WildcardPattern.Compile("HELLO", ignoreCase: true);
        Assert.True(p.TryMatch("hello", out var c));
        Assert.Empty(c);
    }

    [Fact]
    public void GeneralShape_CharsEqual_CaseInsensitive()
    {
        // [a-z] range forces General shape (not promoted to literal)
        // Multi-char literal "Hello" with ignoreCase hits CharsEqual line 365
        var p = WildcardPattern.Compile("[a-z]Hello", ignoreCase: true);
        Assert.True(p.IsMatch("ahello"));
        Assert.True(p.IsMatch("AHELLO"));
        Assert.False(p.IsMatch("1hello"));
    }

    [Fact]
    public void GeneralShape_MultiCharLiteral_CaseSensitive_Mismatch()
    {
        // [a-z] forces General shape, multi-char literal "hello", case-sensitive
        // Exercises MatchLiteral lines 556-559 (SequenceEqual path)
        var p = WildcardPattern.Compile("[a-z]hello");
        Assert.True(p.IsMatch("ahello"));
        Assert.False(p.IsMatch("aHELLO")); // case-sensitive mismatch on multi-char literal
        Assert.False(p.IsMatch("awrong"));
    }

    [Fact]
    public void GeneralShape_ConsecutiveStars_Captures()
    {
        // [a-z] forces General, two stars — second star hits activeStarIdx >= 0 (line 434)
        var p = WildcardPattern.Compile("[a-z]*[a-z]*[a-z]");
        Assert.True(p.TryMatch("aXbYc", out var c));
        Assert.Equal(["X", "Y"], c);
    }

    [Fact]
    public void GeneralShape_StarActive_AtEndOfInput()
    {
        // [a-z]* — General shape, star is active when input ends (line 520)
        var p = WildcardPattern.Compile("[a-z]*");
        Assert.True(p.TryMatch("xrest", out var c));
        Assert.Equal(["rest"], c);
    }

    [Fact]
    public void GeneralShape_TrailingStars_WithCaptures()
    {
        // [!0-9]b** — negated char class forces General, trailing star captures empty
        var p = WildcardPattern.Compile("[!0-9]b**");
        Assert.True(p.TryMatch("ab", out var c));
        Assert.Equal([""], c);
    }

    [Fact]
    public void GeneralShape_Backtracking_WithCaptures()
    {
        // [a-z] forces General shape, backtracking with captures active
        var p = WildcardPattern.Compile("[a-z]*x*y");
        Assert.True(p.TryMatch("aQxRy", out var c));
        Assert.Equal(["Q", "R"], c);
    }

    [Fact]
    public void GeneralShape_SingleCharLiteral_CaseInsensitive()
    {
        // [a-z] + single-char literal "X" forces General shape
        // Single-char literal hits CharsEqual with ignoreCase (line 365)
        var p = WildcardPattern.Compile("[a-z]X", ignoreCase: true);
        Assert.True(p.IsMatch("ax"));  // 'X' vs 'x' → CharsEqual case-insensitive
        Assert.True(p.IsMatch("aX"));
        Assert.False(p.IsMatch("1x")); // [a-z] fails
    }

    [Fact]
    public void GeneralShape_MultiCharLiteral_CaseInsensitive_Mismatch()
    {
        // Multi-char literal in General shape with ignoreCase — MISMATCH path (line 558)
        var p = WildcardPattern.Compile("[a-z]Hello", ignoreCase: true);
        Assert.False(p.IsMatch("athere")); // "there" vs "Hello" → Equals returns false
    }

    // ── Brace alternation ──

    [Fact]
    public void Brace_CapturesFromMatchingAlternative()
    {
        var p = WildcardPattern.Compile("{error,warn}: *");
        Assert.True(p.TryMatch("error: timeout", out var captures));
        Assert.Equal(["timeout"], captures);
    }

    [Fact]
    public void Brace_SecondAlternativeMatches()
    {
        var p = WildcardPattern.Compile("{error,warn}: *");
        Assert.True(p.TryMatch("warn: low memory", out var captures));
        Assert.Equal(["low memory"], captures);
    }

    [Fact]
    public void Brace_NoMatch_ReturnsFalse()
    {
        var p = WildcardPattern.Compile("{error,warn}: *");
        Assert.False(p.TryMatch("info: ok", out var captures));
        Assert.Empty(captures);
    }

    [Fact]
    public void Brace_SuffixAlternation_Captures()
    {
        var p = WildcardPattern.Compile("*.{cs,fs}");
        Assert.True(p.TryMatch("file.cs", out var captures));
        Assert.Equal(["file"], captures);
    }

    [Fact]
    public void Brace_NoBracesNoCaptures()
    {
        var p = WildcardPattern.Compile("{hello,world}");
        Assert.True(p.TryMatch("hello", out var captures));
        Assert.Empty(captures);
    }
}

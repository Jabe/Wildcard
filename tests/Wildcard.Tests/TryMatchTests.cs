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

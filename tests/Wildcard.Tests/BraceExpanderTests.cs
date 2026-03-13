namespace Wildcard.Tests;

public class BraceExpanderTests
{
    private static string[] Expand(string pattern) => BraceExpander.Expand(pattern);

    [Fact]
    public void NoBraces_ReturnsSingleElement()
    {
        Assert.Equal(["hello"], Expand("hello"));
    }

    [Fact]
    public void SimpleAlternatives()
    {
        Assert.Equal(["a", "b"], Expand("{a,b}"));
    }

    [Fact]
    public void PrefixAndSuffix()
    {
        Assert.Equal(["xay", "xby"], Expand("x{a,b}y"));
    }

    [Fact]
    public void ThreeAlternatives()
    {
        Assert.Equal(["*.razor", "*.cs", "*.css"], Expand("*.{razor,cs,css}"));
    }

    [Fact]
    public void CartesianProduct_TwoBraceGroups()
    {
        var result = Expand("{a,b}{c,d}");
        Assert.Equal(["ac", "ad", "bc", "bd"], result);
    }

    [Fact]
    public void NestedBraces()
    {
        Assert.Equal(["a", "b", "c"], Expand("{a,{b,c}}"));
    }

    [Fact]
    public void DeeplyNestedBraces()
    {
        Assert.Equal(["a", "b", "c", "d", "e"], Expand("{a,{b,{c,{d,e}}}}"));
    }

    [Fact]
    public void EscapedOpenBrace_NoExpansion()
    {
        var result = Expand("\\{a,b}");
        Assert.Single(result);
        Assert.Equal("\\{a,b}", result[0]);
    }

    [Fact]
    public void BraceInsideCharClass_NoExpansion()
    {
        var result = Expand("[{]");
        Assert.Single(result);
        Assert.Equal("[{]", result[0]);
    }

    [Fact]
    public void UnmatchedOpenBrace_Literal()
    {
        Assert.Equal(["{abc"], Expand("{abc"));
    }

    [Fact]
    public void EmptyAlternative()
    {
        Assert.Equal(["a", ""], Expand("{a,}"));
    }

    [Fact]
    public void AllEmptyAlternatives()
    {
        Assert.Equal(["", "", ""], Expand("{,,}"));
    }

    [Fact]
    public void SingleAlternative()
    {
        Assert.Equal(["cs"], Expand("{cs}"));
    }

    [Fact]
    public void WithPathSeparators()
    {
        Assert.Equal(["src/**/*.cs", "lib/**/*.cs"], Expand("{src,lib}/**/*.cs"));
    }

    [Fact]
    public void CrossSegmentAlternatives()
    {
        Assert.Equal(["src/utils/file.cs", "docs/file.cs"], Expand("{src/utils,docs}/file.cs"));
    }

    [Fact]
    public void BracesWithWildcards()
    {
        Assert.Equal(["*.cs", "*.razor"], Expand("*.{cs,razor}"));
    }

    [Fact]
    public void MultipleBraceGroupsWithPath()
    {
        var result = Expand("{src,lib}/{a,b}.cs");
        Assert.Equal(["src/a.cs", "src/b.cs", "lib/a.cs", "lib/b.cs"], result);
    }

    [Fact]
    public void ExplosionGuard_BoundedExpansionCount()
    {
        // 2^12 = 4096 potential expansions, but should be capped at 1024
        var pattern = "{a,b}{c,d}{e,f}{g,h}{i,j}{k,l}{m,n}{o,p}{q,r}{s,t}{u,v}{w,x}";
        var result = Expand(pattern);
        Assert.True(result.Length <= 1024);
    }
}

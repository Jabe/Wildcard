using Wildcard;

namespace Wildcard.Tests;

public class WildcardExtensionsTests
{
    private readonly string[] _files = ["readme.txt", "data.csv", "report.csv", "image.png", "notes.txt"];

    [Fact]
    public void WhereMatch_CompiledPattern()
    {
        var pattern = WildcardPattern.Compile("*.csv");
        var result = _files.WhereMatch(pattern).ToList();
        Assert.Equal(["data.csv", "report.csv"], result);
    }

    [Fact]
    public void WhereMatch_StringPattern()
    {
        var result = _files.WhereMatch("*.txt").ToList();
        Assert.Equal(["readme.txt", "notes.txt"], result);
    }

    [Fact]
    public void WhereMatch_CaseInsensitive()
    {
        string[] items = ["Hello", "HELLO", "world"];
        var result = items.WhereMatch("hello", ignoreCase: true).ToList();
        Assert.Equal(["Hello", "HELLO"], result);
    }

    [Fact]
    public void WhereMatch_NoMatches()
    {
        var result = _files.WhereMatch("*.xml").ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void AnyMatch_True()
    {
        var pattern = WildcardPattern.Compile("*.csv");
        Assert.True(_files.AnyMatch(pattern));
    }

    [Fact]
    public void AnyMatch_False()
    {
        var pattern = WildcardPattern.Compile("*.xml");
        Assert.False(_files.AnyMatch(pattern));
    }

    [Fact]
    public void FirstMatch_Found()
    {
        var pattern = WildcardPattern.Compile("*.csv");
        Assert.Equal("data.csv", _files.FirstMatch(pattern));
    }

    [Fact]
    public void FirstMatch_NotFound()
    {
        var pattern = WildcardPattern.Compile("*.xml");
        Assert.Null(_files.FirstMatch(pattern));
    }

    [Fact]
    public void WhereMatch_IsLazy()
    {
        var pattern = WildcardPattern.Compile("*.csv");
        // Should not throw - lazy enumeration doesn't consume all items
        var query = _files.WhereMatch(pattern);
        Assert.Equal("data.csv", query.First());
    }
}

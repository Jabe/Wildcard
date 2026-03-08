using Wildcard;

namespace Wildcard.Tests;

public class WildcardSearchTests
{
    [Fact]
    public void FilterLines_ReturnsMatches()
    {
        var pattern = WildcardPattern.Compile("*.cs");
        var lines = new[] { "Program.cs", "readme.md", "Startup.cs", "data.json" };
        var result = WildcardSearch.FilterLines(pattern, lines);
        Assert.Equal(new[] { "Program.cs", "Startup.cs" }, result);
    }

    [Fact]
    public void FilterLines_EmptyInput_ReturnsEmpty()
    {
        var pattern = WildcardPattern.Compile("*");
        var result = WildcardSearch.FilterLines(pattern, Array.Empty<string>());
        Assert.Empty(result);
    }

    [Fact]
    public void FilterLinesWithIndex_ReturnsMatchesAndIndices()
    {
        var pattern = WildcardPattern.Compile("error*");
        var lines = new[] { "info: ok", "error: fail", "warn: maybe", "error: crash" };
        var result = WildcardSearch.FilterLinesWithIndex(pattern, lines);

        Assert.Equal(2, result.Count);
        Assert.Equal((1, "error: fail"), result[0]);
        Assert.Equal((3, "error: crash"), result[1]);
    }

    [Fact]
    public void FilterBulk_Sequential()
    {
        var pattern = WildcardPattern.Compile("test_[0-9]*");
        var inputs = new[] { "test_1", "test_2", "prod_1", "test_a", "test_99" };
        var result = WildcardSearch.FilterBulk(pattern, inputs);
        Assert.Equal(new[] { "test_1", "test_2", "test_99" }, result);
    }

    [Fact]
    public void FilterBulk_Parallel_LargeInput()
    {
        var pattern = WildcardPattern.Compile("match_*");
        var inputs = Enumerable.Range(0, 2000)
            .Select(i => i % 3 == 0 ? $"match_{i}" : $"skip_{i}")
            .ToArray();

        var result = WildcardSearch.FilterBulk(pattern, inputs, parallel: true);
        // Parallel doesn't guarantee order, so check count
        int expected = Enumerable.Range(0, 2000).Count(i => i % 3 == 0);
        Assert.Equal(expected, result.Length);
        Assert.All(result, s => Assert.StartsWith("match_", s));
    }

    [Fact]
    public void FindAllPositions_FindsSubstringMatches()
    {
        var pattern = WildcardPattern.Compile("[A-Z][a-z]*");
        var text = "Hello World Foo";
        var positions = WildcardSearch.FindAllPositions(pattern, text.AsSpan(), maxLength: 10);
        // Should find positions 0 (Hello), 6 (World), 12 (Foo)
        Assert.Contains(0, positions);
        Assert.Contains(6, positions);
        Assert.Contains(12, positions);
    }

    [Fact]
    public void VectorizedIndexOf_FindsChar()
    {
        var text = "hello world".AsSpan();
        Assert.Equal(4, WildcardSearch.VectorizedIndexOf(text, 'o'));
        Assert.Equal(-1, WildcardSearch.VectorizedIndexOf(text, 'z'));
    }
}

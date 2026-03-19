namespace Wildcard.Tests;

public class TreeRendererTests
{
    [Fact]
    public void EmptyInput_ReturnsRootOnly()
    {
        var result = TreeRenderer.Render([]);
        Assert.Contains(".", result);
        Assert.Contains("0 files", result);
    }

    [Fact]
    public void SingleFile_RendersCorrectly()
    {
        var result = TreeRenderer.Render(["readme.md"]);
        Assert.Contains("└── readme.md", result);
        Assert.Contains("1 file", result);
    }

    [Fact]
    public void FlatFiles_RendersAllWithConnectors()
    {
        var result = TreeRenderer.Render(["a.txt", "b.txt", "c.txt"]);
        Assert.Contains("├── a.txt", result);
        Assert.Contains("├── b.txt", result);
        Assert.Contains("└── c.txt", result);
        Assert.Contains("3 files", result);
    }

    [Fact]
    public void NestedDirectories_RendersTree()
    {
        var paths = new[]
        {
            "src/app.cs",
            "src/utils/helper.cs",
            "tests/test.cs",
        };

        var result = TreeRenderer.Render(paths);

        Assert.Contains("src/", result);
        Assert.Contains("├── app.cs", result);
        Assert.Contains("└── helper.cs", result);
        Assert.Contains("tests/", result);
        Assert.Contains("└── test.cs", result);
        Assert.Contains("3 files", result);
    }

    [Fact]
    public void DepthCap_TruncatesDeepPaths()
    {
        var paths = new[]
        {
            "a/b/c/d/e/deep.txt",
            "a/shallow.txt",
        };

        var result = TreeRenderer.Render(paths, maxDepth: 2);

        // maxDepth=2: a/ (depth 0), its children (depth 1): b/ and shallow.txt
        // b/ is a dir at depth 1, its children would be depth 2 = cap, so "..."
        Assert.Contains("a/", result);
        Assert.Contains("shallow.txt", result);
        Assert.Contains("b/", result);
        Assert.Contains("...", result);
        Assert.Contains("2 files", result);
    }

    [Fact]
    public void DepthCap_One_ShowsOnlyTopLevel()
    {
        var paths = new[]
        {
            "src/file.cs",
            "readme.md",
        };

        var result = TreeRenderer.Render(paths, maxDepth: 1);

        Assert.Contains("readme.md", result);
        Assert.Contains("src/", result);
        Assert.Contains("...", result);
    }

    [Fact]
    public void UnlimitedDepth_WithZero()
    {
        var paths = new[]
        {
            "a/b/c/d/deep.txt",
        };

        var result = TreeRenderer.Render(paths, maxDepth: 0);

        Assert.Contains("deep.txt", result);
        Assert.DoesNotContain("...", result);
    }

    [Fact]
    public void SortedOutput()
    {
        var paths = new[] { "z.txt", "a.txt", "m.txt" };

        var result = TreeRenderer.Render(paths);
        int aPos = result.IndexOf("a.txt");
        int mPos = result.IndexOf("m.txt");
        int zPos = result.IndexOf("z.txt");

        Assert.True(aPos < mPos, "a.txt should appear before m.txt");
        Assert.True(mPos < zPos, "m.txt should appear before z.txt");
    }

    [Fact]
    public void DirsAndFiles_MixedCorrectly()
    {
        var paths = new[]
        {
            "src/a.cs",
            "src/b.cs",
            "readme.md",
        };

        var result = TreeRenderer.Render(paths);

        // readme.md (file) and src/ (dir) should both appear at root level
        Assert.Contains("readme.md", result);
        Assert.Contains("src/", result);
    }
}

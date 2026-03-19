using Wildcard.Mcp.Tools;

namespace Wildcard.Tests;

public class GlobToolTests : IDisposable
{
    private readonly string _tempDir;

    public GlobToolTests()
    {
        _tempDir = Path.Combine(Directory.GetCurrentDirectory(), ".test_glob_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private void CreateFile(string relativePath, string content = "")
    {
        var fullPath = Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    [Fact]
    public async Task Tree_RendersAsTree()
    {
        CreateFile("src/app.cs");
        CreateFile("src/utils/helper.cs");
        CreateFile("readme.md");

        var result = await GlobTool.Glob("**/*", base_directory: _tempDir, tree: true, respect_gitignore: false);
        Assert.Contains("app.cs", result);
        Assert.Contains("utils/", result);
        Assert.Contains("helper.cs", result);
        Assert.Contains("readme.md", result);
        // Tree connectors
        Assert.True(result.Contains("├──") || result.Contains("└──"), "Should contain tree connectors");
    }

    [Fact]
    public async Task Tree_RespectsMaxDepth()
    {
        CreateFile("a/b/c/deep.txt");
        CreateFile("a/shallow.txt");

        var result = await GlobTool.Glob("**/*", base_directory: _tempDir, tree: true, max_depth: 2, respect_gitignore: false);

        Assert.Contains("a/", result);
        Assert.Contains("shallow.txt", result);
        Assert.Contains("...", result);
    }

    [Fact]
    public async Task Tree_CountWins_OverTree()
    {
        CreateFile("a.txt");
        CreateFile("b.txt");

        var result = await GlobTool.Glob("**/*", base_directory: _tempDir, tree: true, count: true, respect_gitignore: false);

        // count should take precedence
        Assert.Contains("files found", result);
        Assert.DoesNotContain("├──", result);
        Assert.DoesNotContain("└──", result);
    }

    [Fact]
    public async Task Tree_EmptyResult()
    {
        var result = await GlobTool.Glob("**/*.nonexistent", base_directory: _tempDir, tree: true, respect_gitignore: false);

        Assert.Contains("No files found", result);
    }

    [Fact]
    public async Task Tree_WithLimit()
    {
        for (int i = 0; i < 10; i++)
            CreateFile($"file{i}.txt");

        var result = await GlobTool.Glob("**/*", base_directory: _tempDir, tree: true, limit: 3, respect_gitignore: false);

        Assert.Contains("more files", result);
    }
}

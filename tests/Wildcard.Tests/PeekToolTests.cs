using ModelContextProtocol.Server;
using Wildcard.Mcp;
using Wildcard.Mcp.Tools;

namespace Wildcard.Tests;

public class PeekToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RootsProvider _rootsProvider;

    public PeekToolTests()
    {
        _tempDir = Path.Combine(Directory.GetCurrentDirectory(), ".test_peek_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _rootsProvider = new RootsProvider();
        _rootsProvider.SetRoots([_tempDir]);
    }

    public void Dispose()
    {
        _rootsProvider.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private void CreateFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    [Fact]
    public async Task SingleFile_ReadsContent()
    {
        CreateFile("test.txt", "hello\nworld\n");

        var result = await PeekTool.Peek(["test.txt"], rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir);

        Assert.Contains("test.txt", result);
        Assert.Contains("hello", result);
        Assert.Contains("world", result);
    }

    [Fact]
    public async Task MultipleFiles_ReadsAll()
    {
        CreateFile("a.txt", "alpha\n");
        CreateFile("b.txt", "beta\n");

        var result = await PeekTool.Peek(["a.txt", "b.txt"], rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir);

        Assert.Contains("a.txt", result);
        Assert.Contains("alpha", result);
        Assert.Contains("b.txt", result);
        Assert.Contains("beta", result);
    }

    [Fact]
    public async Task LineRange_ReadsSpecificLines()
    {
        CreateFile("range.txt", "line1\nline2\nline3\nline4\nline5\n");

        var result = await PeekTool.Peek(["range.txt"],
            rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir,
            start_lines: [2], end_lines: [4]);

        Assert.Contains("line2", result);
        Assert.Contains("line3", result);
        Assert.Contains("line4", result);
        Assert.DoesNotContain("line1", result);
        Assert.DoesNotContain("line5", result);
    }

    [Fact]
    public async Task Budget_TruncatesWhenExceeded()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 200; i++)
            sb.AppendLine($"This is a rather long line number {i} with plenty of content to fill the budget quickly and ensure truncation occurs properly.");
        CreateFile("big.txt", sb.ToString());
        CreateFile("small.txt", "should not appear\n");

        var result = await PeekTool.Peek(["big.txt", "small.txt"], rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, max_chars: 200);

        Assert.Contains("big.txt", result);
        // Should either show budget exceeded or truncated
        Assert.True(
            result.Contains("budget exceeded") || result.Contains("truncated"),
            "Should indicate budget limit was reached");
    }

    [Fact]
    public async Task FileNotFound_ReportsError()
    {
        var result = await PeekTool.Peek(["nonexistent.txt"], rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir);

        Assert.Contains("File not found", result);
    }

    [Fact]
    public async Task EmptyFiles_ReportsEmpty()
    {
        CreateFile("empty.txt", "");

        var result = await PeekTool.Peek(["empty.txt"], rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir);

        Assert.Contains("empty", result);
    }

    [Fact]
    public async Task NoFiles_ReportsNoFiles()
    {
        var result = await PeekTool.Peek([], rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir);

        Assert.Contains("No files specified", result);
    }

    [Fact]
    public async Task RelativePaths_ResolvedCorrectly()
    {
        CreateFile("sub/file.txt", "content\n");

        var result = await PeekTool.Peek(["sub/file.txt"], rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir);

        Assert.Contains("sub/file.txt", result);
        Assert.Contains("content", result);
    }

    [Fact]
    public async Task LineNumbers_AreIncluded()
    {
        CreateFile("numbered.txt", "first\nsecond\nthird\n");

        var result = await PeekTool.Peek(["numbered.txt"], rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir);

        Assert.Contains("1:", result);
        Assert.Contains("2:", result);
        Assert.Contains("3:", result);
    }
}

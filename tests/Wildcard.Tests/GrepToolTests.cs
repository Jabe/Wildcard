using ModelContextProtocol.Server;
using Wildcard.Mcp;
using Wildcard.Mcp.Tools;

namespace Wildcard.Tests;

public class GrepToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RootsProvider _rootsProvider;

    public GrepToolTests()
    {
        _tempDir = Path.Combine(Directory.GetCurrentDirectory(), ".test_grep_" + Guid.NewGuid().ToString("N")[..8]);
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

    // --- readLines (file reader mode, no content patterns) ---

    [Fact]
    public async Task ReadLines_NoPatterns_ReadsFirstNLines()
    {
        CreateFile("hello.txt", "line1\nline2\nline3\nline4\nline5\n");

        var result = await GrepTool.Grep("hello.txt", rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false, read_lines: 3);

        Assert.Contains("hello.txt", result);
        Assert.Contains("line1", result);
        Assert.Contains("line2", result);
        Assert.Contains("line3", result);
        Assert.DoesNotContain("line4", result);
    }

    [Fact]
    public async Task ReadLines_NoPatterns_DefaultsTo200Lines()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= 250; i++)
            sb.AppendLine($"line {i}");
        CreateFile("big.txt", sb.ToString());

        var result = await GrepTool.Grep("big.txt", rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false);

        Assert.Contains("line 1", result);
        Assert.Contains("line 200", result);
        Assert.DoesNotContain("line 201", result);
    }

    [Fact]
    public async Task ReadLines_NoPatterns_MultipleFiles()
    {
        CreateFile("a.txt", "alpha\nbeta\n");
        CreateFile("b.txt", "gamma\ndelta\n");

        var result = await GrepTool.Grep("*.txt", rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false, read_lines: 10);

        Assert.Contains("a.txt", result);
        Assert.Contains("alpha", result);
        Assert.Contains("b.txt", result);
        Assert.Contains("gamma", result);
    }

    [Fact]
    public async Task ReadLines_NoPatterns_RespectsMaxFiles()
    {
        for (int i = 0; i < 5; i++)
            CreateFile($"file{i}.txt", $"content{i}\n");

        var result = await GrepTool.Grep("*.txt", rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false, read_lines: 10, max_files: 2);

        Assert.Contains("more files", result);
    }

    // --- readLines with content patterns (expanded context) ---

    [Fact]
    public async Task ReadLines_WithPatterns_ExpandsContext()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= 50; i++)
            sb.AppendLine(i == 25 ? "MATCH_HERE" : $"line {i}");
        CreateFile("ctx.txt", sb.ToString());

        var result = await GrepTool.Grep("ctx.txt", rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false,
            content_patterns: ["MATCH_HERE"], read_lines: 10);

        Assert.Contains("MATCH_HERE", result);
        Assert.Contains("ctx.txt", result);
    }

    // --- allOf (AND mode) ---

    [Fact]
    public async Task AllOf_FileMustContainAllPatterns()
    {
        CreateFile("both.cs", "using ILogger;\nusing IOrderService;\n");
        CreateFile("logger_only.cs", "using ILogger;\nclass Foo {}\n");
        CreateFile("order_only.cs", "using IOrderService;\nclass Bar {}\n");

        var result = await GrepTool.Grep("*.cs", rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false,
            content_patterns: ["ILogger", "IOrderService"],
            all_of: true, files_only: true);

        Assert.Contains("both.cs", result);
        Assert.DoesNotContain("logger_only.cs", result);
        Assert.DoesNotContain("order_only.cs", result);
    }

    [Fact]
    public async Task AllOf_False_OrMode_ReturnsAll()
    {
        CreateFile("both.cs", "using ILogger;\nusing IOrderService;\n");
        CreateFile("logger_only.cs", "using ILogger;\nclass Foo {}\n");

        var result = await GrepTool.Grep("*.cs", rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false,
            content_patterns: ["ILogger", "IOrderService"],
            all_of: false, files_only: true);

        Assert.Contains("both.cs", result);
        Assert.Contains("logger_only.cs", result);
    }

    [Fact]
    public async Task AllOf_SinglePattern_WorksSameAsOr()
    {
        CreateFile("test.cs", "using ILogger;\n");

        var result = await GrepTool.Grep("*.cs", rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false,
            content_patterns: ["ILogger"],
            all_of: true, files_only: true);

        Assert.Contains("test.cs", result);
    }

    // --- maxFiles ---

    [Fact]
    public async Task MaxFiles_LimitsOutputFiles()
    {
        for (int i = 0; i < 10; i++)
            CreateFile($"f{i}.txt", "ERROR found\n");

        var result = await GrepTool.Grep("*.txt", rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false,
            content_patterns: ["ERROR"],
            max_files: 3);

        Assert.Contains("more files matched", result);
    }

    // --- maxMatchesPerFile ---

    [Fact]
    public async Task MaxMatchesPerFile_TruncatesPerFile()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 30; i++)
            sb.AppendLine($"ERROR line {i}");
        CreateFile("errors.txt", sb.ToString());

        var result = await GrepTool.Grep("errors.txt", rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false,
            content_patterns: ["ERROR"],
            max_matches_per_file: 5);

        Assert.Contains("more matches in this file", result);
    }

    // --- count mode with allOf ---

    [Fact]
    public async Task Count_WithAllOf()
    {
        CreateFile("both.cs", "ILogger\nIOrderService\n");
        CreateFile("one.cs", "ILogger\n");

        var result = await GrepTool.Grep("*.cs", rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false,
            content_patterns: ["ILogger", "IOrderService"],
            all_of: true, count: true);

        Assert.Contains("1 file", result);
    }
}

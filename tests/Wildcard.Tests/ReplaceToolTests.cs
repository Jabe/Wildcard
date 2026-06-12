using Wildcard.Mcp;
using Wildcard.Mcp.Tools;

namespace Wildcard.Tests;

public class ReplaceToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RootsProvider _rootsProvider;

    public ReplaceToolTests()
    {
        _tempDir = Path.Combine(Directory.GetCurrentDirectory(), ".test_replace_" + Guid.NewGuid().ToString("N")[..8]);
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

    private string ReadFile(string relativePath) =>
        File.ReadAllText(Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    // --- Glob pattern parity with grep (bare relative paths, brace expansion) ---

    [Fact]
    public async Task BareRelativePath_AsPattern_Works()
    {
        CreateFile(".github/workflows/publish.yml", "name: publish\non: push\n");

        var result = await ReplaceTool.Replace(".github/workflows/publish.yml", "publish", "deploy",
            rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false);

        Assert.Contains("publish.yml", result);
        Assert.Contains("deploy", result);
        Assert.Contains("dry-run", result);
    }

    [Fact]
    public async Task BraceExpansion_AsPattern_MatchesOnlyListedAlternatives()
    {
        CreateFile("src/Components/FlareModal.razor.cs", "OldName here\n");
        CreateFile("src/Components/FlareConfirm.razor.cs", "OldName there\n");
        CreateFile("src/Components/FlareOther.razor.cs", "OldName elsewhere\n");

        var result = await ReplaceTool.Replace("src/Components/Flare{Modal,Confirm}.razor.cs", "OldName", "NewName",
            rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false);

        Assert.Contains("FlareModal.razor.cs", result);
        Assert.Contains("FlareConfirm.razor.cs", result);
        Assert.DoesNotContain("FlareOther.razor.cs", result);
    }

    [Fact]
    public async Task BraceExpansion_WithRecursiveGlob_Works()
    {
        CreateFile("src/Components/FlareModal.razor.cs", "OldName here\n");
        CreateFile("src/Components/FlareConfirm.razor.cs", "OldName there\n");

        var result = await ReplaceTool.Replace("**/Flare{Modal,Confirm}.razor.cs", "OldName", "NewName",
            rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false);

        Assert.Contains("FlareModal.razor.cs", result);
        Assert.Contains("FlareConfirm.razor.cs", result);
    }

    [Fact]
    public async Task GrepAndReplace_SamePattern_FindSameFiles()
    {
        CreateFile("a/FlareModal.razor.cs", "needle\n");
        CreateFile("b/FlareConfirm.razor.cs", "needle\n");
        CreateFile("c/FlareOther.razor.cs", "needle\n");

        var pattern = "**/Flare{Modal,Confirm}.razor.cs";

        var grepResult = await GrepTool.Grep(pattern, content_patterns: ["needle"], files_only: true,
            rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false);
        var replaceResult = await ReplaceTool.Replace(pattern, "needle", "thread",
            rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false);

        foreach (var file in new[] { "FlareModal.razor.cs", "FlareConfirm.razor.cs" })
        {
            Assert.Contains(file, grepResult);
            Assert.Contains(file, replaceResult);
        }
        Assert.DoesNotContain("FlareOther.razor.cs", grepResult);
        Assert.DoesNotContain("FlareOther.razor.cs", replaceResult);
    }

    // --- Error messages distinguish glob miss from content miss ---

    [Fact]
    public async Task NoGlobMatch_ReportsPatternMiss()
    {
        CreateFile("a.txt", "hello\n");

        var result = await ReplaceTool.Replace("does/not/exist/*.xyz", "hello", "bye",
            rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false);

        Assert.Contains("No files matched pattern", result);
        Assert.Contains("does/not/exist/*.xyz", result);
    }

    [Fact]
    public async Task GlobMatchesButNoContent_ReportsContentMiss()
    {
        CreateFile("a.txt", "hello\n");
        CreateFile("b.txt", "world\n");

        var result = await ReplaceTool.Replace("*.txt", "NOTFOUND", "x",
            rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false);

        Assert.Contains("2 files matched pattern", result);
        Assert.Contains("none contained the find text", result);
    }

    // --- Multi-line literal find ---

    [Fact]
    public async Task MultiLineFind_DryRun_PreviewsAllLines()
    {
        CreateFile("Service.cs", "public void A()\n{\n    DoOld();\n    DoOld2();\n}\n");

        var result = await ReplaceTool.Replace("Service.cs", "    DoOld();\n    DoOld2();", "    DoNew();",
            rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false);

        Assert.Contains("- ", result);
        Assert.Contains("DoOld();", result);
        Assert.Contains("DoOld2();", result);
        Assert.Contains("+ ", result);
        Assert.Contains("DoNew();", result);
        Assert.Contains("dry-run", result);

        // Dry-run must not write
        Assert.Contains("DoOld();", ReadFile("Service.cs"));
    }

    [Fact]
    public async Task MultiLineFind_Apply_WritesFile()
    {
        CreateFile("Service.cs", "public void A()\n{\n    DoOld();\n    DoOld2();\n}\n");

        var result = await ReplaceTool.Replace("Service.cs", "    DoOld();\n    DoOld2();", "    DoNew();",
            dry_run: false,
            rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false);

        Assert.Contains("applied", result);
        Assert.Equal("public void A()\n{\n    DoNew();\n}\n", ReadFile("Service.cs"));
    }

    [Fact]
    public async Task MultiLineFind_WithWildcardChars_MatchesLiterally()
    {
        CreateFile("Code.cs", "/* header */\nint[] data = arr[0];\nvar x = a * b;\n");

        var result = await ReplaceTool.Replace("Code.cs", "int[] data = arr[0];\nvar x = a * b;", "int data;",
            dry_run: false,
            rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false);

        Assert.Contains("applied", result);
        Assert.Equal("/* header */\nint data;\n", ReadFile("Code.cs"));
    }

    [Fact]
    public async Task MultiLineFind_CrlfFile_ReportsLineEndingNormalization()
    {
        var path = Path.Combine(_tempDir, "Crlf.cs");
        File.WriteAllText(path, "alpha\r\nbeta\r\ngamma\r\n");

        var result = await ReplaceTool.Replace("Crlf.cs", "alpha\nbeta", "one\ntwo",
            dry_run: false,
            rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false);

        Assert.Contains("line endings normalized to CRLF to match file", result);
        Assert.Equal("one\r\ntwo\r\ngamma\r\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task MultiLineFind_LfFile_NoLineEndingNote()
    {
        CreateFile("Lf.cs", "alpha\nbeta\ngamma\n");

        var result = await ReplaceTool.Replace("Lf.cs", "alpha\nbeta", "one\ntwo",
            rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false);

        Assert.DoesNotContain("line endings normalized", result);
    }

    [Fact]
    public async Task MultiLineFind_NoContentMatch_ReportsContentMiss()
    {
        CreateFile("a.cs", "alpha\nbeta\n");

        var result = await ReplaceTool.Replace("a.cs", "alpha\nNOTTHERE", "x",
            rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false);

        Assert.Contains("none contained the find text", result);
    }

    // --- Single-line behavior still intact ---

    [Fact]
    public async Task SingleLineFind_Apply_WritesFile()
    {
        CreateFile("a.txt", "hello world\n");

        var result = await ReplaceTool.Replace("a.txt", "hello", "goodbye",
            dry_run: false,
            rootsProvider: _rootsProvider, server: null!, base_directory: _tempDir, respect_gitignore: false);

        Assert.Contains("applied", result);
        Assert.Equal("goodbye world\n", ReadFile("a.txt"));
    }
}

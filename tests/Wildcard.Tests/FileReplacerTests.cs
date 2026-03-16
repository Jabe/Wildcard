using System.Text;

namespace Wildcard.Tests;

public class FileReplacerTests : IDisposable
{
    private readonly string _tempDir;

    public FileReplacerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wildcard_replace_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string CreateFile(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        return path;
    }

    // --- Preview tests ---

    [Fact]
    public void Preview_LiteralReplace_FindsReplacements()
    {
        var file = CreateFile("test.cs", "throw new Exception(\"ERROR: failed\");\nConsole.WriteLine(\"OK\");\n");
        var results = FileReplacer.Preview([file], "ERROR", "WARNING");

        Assert.Single(results);
        Assert.Single(results[0].Replacements);
        var r = results[0].Replacements[0];
        Assert.Equal(1, r.LineNumber);
        Assert.Contains("ERROR", r.OriginalLine);
        Assert.Contains("WARNING", r.ReplacedLine);
        Assert.DoesNotContain("ERROR", r.ReplacedLine);
    }

    [Fact]
    public void Preview_NoMatch_ReturnsEmpty()
    {
        var file = CreateFile("test.txt", "hello world\nfoo bar\n");
        var results = FileReplacer.Preview([file], "NOTFOUND", "replacement");
        Assert.Empty(results);
    }

    [Fact]
    public void Preview_DoesNotModifyFiles()
    {
        var content = "ERROR: something\nERROR: another\n";
        var file = CreateFile("test.txt", content);
        FileReplacer.Preview([file], "ERROR", "WARNING");

        Assert.Equal(content, File.ReadAllText(file));
    }

    // --- Apply tests ---

    [Fact]
    public void Apply_WritesChanges()
    {
        var file = CreateFile("test.txt", "old value here\nkeep this\n");
        var results = FileReplacer.Apply([file], "old", "new");

        Assert.Single(results);
        Assert.Single(results[0].Replacements);

        var content = File.ReadAllText(file);
        Assert.Contains("new value here", content);
        Assert.Contains("keep this", content);
        Assert.DoesNotContain("old value", content);
    }

    [Fact]
    public void Apply_PreservesEncoding_BOM()
    {
        var file = CreateFile("bom.txt", "ERROR line\nOK line\n", new UTF8Encoding(true));

        // Verify BOM exists before
        var bytesBefore = File.ReadAllBytes(file);
        Assert.True(bytesBefore.Length >= 3 && bytesBefore[0] == 0xEF && bytesBefore[1] == 0xBB && bytesBefore[2] == 0xBF);

        FileReplacer.Apply([file], "ERROR", "WARNING");

        // Verify BOM preserved
        var bytesAfter = File.ReadAllBytes(file);
        Assert.True(bytesAfter.Length >= 3 && bytesAfter[0] == 0xEF && bytesAfter[1] == 0xBB && bytesAfter[2] == 0xBF);
        Assert.Contains("WARNING line", File.ReadAllText(file));
    }

    [Fact]
    public void Apply_PreservesLineEndings_CRLF()
    {
        var file = Path.Combine(_tempDir, "crlf.txt");
        File.WriteAllText(file, "first ERROR line\r\nsecond line\r\n", new UTF8Encoding(false));

        FileReplacer.Apply([file], "ERROR", "WARNING");

        var content = File.ReadAllText(file);
        Assert.Contains("\r\n", content);
        Assert.Contains("WARNING", content);
    }

    [Fact]
    public void Apply_PreservesLineEndings_LF()
    {
        var file = CreateFile("lf.txt", "first ERROR line\nsecond line\n");

        FileReplacer.Apply([file], "ERROR", "WARNING");

        var content = File.ReadAllText(file);
        Assert.DoesNotContain("\r\n", content);
        Assert.Contains("WARNING", content);
    }

    // --- Case-insensitive ---

    [Fact]
    public void Replace_CaseInsensitive()
    {
        var file = CreateFile("test.txt", "Error here\nerror there\nERROR everywhere\n");
        var results = FileReplacer.Preview([file], "error", "WARNING", ignoreCase: true);

        Assert.Single(results);
        Assert.Equal(3, results[0].Replacements.Count);
        foreach (var r in results[0].Replacements)
            Assert.Contains("WARNING", r.ReplacedLine);
    }

    // --- Multiple occurrences ---

    [Fact]
    public void Replace_MultipleOccurrencesPerLine()
    {
        var file = CreateFile("test.txt", "ERROR and ERROR and ERROR\n");
        var results = FileReplacer.Preview([file], "ERROR", "WARN");

        Assert.Single(results);
        var r = results[0].Replacements[0];
        Assert.Equal("WARN and WARN and WARN", r.ReplacedLine);
    }

    // --- Multiple files ---

    [Fact]
    public void Replace_MultipleFiles()
    {
        var file1 = CreateFile("a.txt", "ERROR in file a\n");
        var file2 = CreateFile("b.txt", "no matches here\n");
        var file3 = CreateFile("c.txt", "ERROR in file c\n");

        var results = FileReplacer.Preview([file1, file2, file3], "ERROR", "WARNING");
        Assert.Equal(2, results.Count);
    }

    // --- Empty replacement (deletion) ---

    [Fact]
    public void Replace_EmptyReplacement()
    {
        var file = CreateFile("test.txt", "prefix ERROR suffix\n");
        var results = FileReplacer.Preview([file], "ERROR ", "");

        Assert.Single(results);
        Assert.Equal("prefix suffix", results[0].Replacements[0].ReplacedLine);
    }

    // --- Binary file skip ---

    [Fact]
    public void Replace_SkipsBinaryFiles()
    {
        var file = Path.Combine(_tempDir, "binary.bin");
        var bytes = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0x57, 0x6F, 0x72, 0x6C, 0x64 }; // "Hello\0World"
        File.WriteAllBytes(file, bytes);

        var results = FileReplacer.Preview([file], "Hello", "Goodbye");
        Assert.Empty(results);
    }

    // --- Capture group replacement ---

    [Fact]
    public void Replace_CaptureGroup()
    {
        var file = CreateFile("test.cs", "console.log(\"hello\")\nconsole.log(\"world\")\nkeep this\n");
        var results = FileReplacer.Preview([file], "*console.log(*)*", "$1logger.info($2)$3");

        Assert.Single(results);
        Assert.Equal(2, results[0].Replacements.Count);
        Assert.Equal("logger.info(\"hello\")", results[0].Replacements[0].ReplacedLine);
        Assert.Equal("logger.info(\"world\")", results[0].Replacements[1].ReplacedLine);
    }

    // --- IsLiteralPattern ---

    [Fact]
    public void IsLiteralPattern_PlainString_True()
    {
        Assert.True(FileReplacer.IsLiteralPattern("hello"));
        Assert.True(FileReplacer.IsLiteralPattern("ERROR"));
        Assert.True(FileReplacer.IsLiteralPattern("some.method()"));
    }

    [Fact]
    public void IsLiteralPattern_WithWildcards_False()
    {
        Assert.False(FileReplacer.IsLiteralPattern("*ERROR*"));
        Assert.False(FileReplacer.IsLiteralPattern("file?.txt"));
        Assert.False(FileReplacer.IsLiteralPattern("[abc]"));
    }

    [Fact]
    public void IsLiteralPattern_EscapedWildcard_True()
    {
        Assert.True(FileReplacer.IsLiteralPattern("\\*escaped\\*"));
    }

    // --- BuildCaptureReplacement ---

    [Fact]
    public void BuildCaptureReplacement_SubstitutesCorrectly()
    {
        var result = FileReplacer.BuildCaptureReplacement("$1-replaced-$2", ["before", "after"]);
        Assert.Equal("before-replaced-after", result);
    }

    [Fact]
    public void BuildCaptureReplacement_OutOfRangeLeftAsIs()
    {
        var result = FileReplacer.BuildCaptureReplacement("$1 and $3", ["val1"]);
        Assert.Equal("val1 and $3", result);
    }

    // --- Empty file ---

    [Fact]
    public void Replace_EmptyFile_NoError()
    {
        var file = CreateFile("empty.txt", "");
        var results = FileReplacer.Preview([file], "something", "other");
        Assert.Empty(results);
    }

    // --- Read-only file skip ---

    [Fact]
    public void Replace_SkipsReadOnlyFiles()
    {
        var file = CreateFile("readonly.txt", "ERROR here\n");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            var results = FileReplacer.Preview([file], "ERROR", "WARNING");
            Assert.Empty(results);
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    // --- Per-file error resilience ---

    [Fact]
    public void Apply_ContinuesAfterWriteFailure_ReportsError()
    {
        if (OperatingSystem.IsWindows())
            return; // SetUnixFileMode not supported on Windows

        // Put the "bad" file in a subdirectory, then make that dir read-only
        // so the temp file write fails — but the file itself is still readable
        var lockedDir = Path.Combine(_tempDir, "locked");
        Directory.CreateDirectory(lockedDir);
        var bad = Path.Combine(lockedDir, "fail.txt");
        File.WriteAllText(bad, "ERROR bad\n");

        var good1 = CreateFile("a.txt", "ERROR here\n");
        var good2 = CreateFile("c.txt", "ERROR there\n");

        // Remove write permission on the directory so temp file creation fails
        File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var results = FileReplacer.Apply([good1, bad, good2], "ERROR", "WARNING");

            // Both good files should succeed
            var succeeded = results.Where(r => r.Error is null && r.Replacements.Count > 0).ToList();
            Assert.Equal(2, succeeded.Count);
            Assert.All(succeeded, r => Assert.Contains("WARNING", r.Replacements[0].ReplacedLine));

            // Verify good files were actually written to disk
            Assert.Contains("WARNING", File.ReadAllText(good1));
            Assert.Contains("WARNING", File.ReadAllText(good2));

            // The bad file should have an error
            var errors = results.Where(r => r.Error is not null).ToList();
            Assert.Single(errors);
            Assert.Contains("locked", errors[0].FilePath);
            Assert.NotNull(errors[0].Error);

            // Original bad file should be untouched
            Assert.Contains("ERROR", File.ReadAllText(bad));
        }
        finally
        {
            File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // --- Wildcard pattern Apply ---

    [Fact]
    public void Apply_WildcardPattern_AppliesCaptureReplacement()
    {
        var file = CreateFile("pattern.txt", "prefix_old_suffix\nanother_old_line\nno match here\n");
        var results = FileReplacer.Apply([file], "*old*", "$1new$2");

        Assert.Single(results);
        Assert.Equal(2, results[0].Replacements.Count);
        Assert.Equal("prefix_new_suffix", results[0].Replacements[0].ReplacedLine);
        Assert.Equal("another_new_line", results[0].Replacements[1].ReplacedLine);

        var content = File.ReadAllText(file);
        Assert.Contains("prefix_new_suffix", content);
        Assert.Contains("another_new_line", content);
        Assert.DoesNotContain("old", content);
    }

    [Fact]
    public void Apply_WildcardPattern_NoActualChanges_ReturnsEmpty()
    {
        // Pattern matches entire line via *, but replacement reproduces the original
        var file = CreateFile("nochange.txt", "hello world\n");
        var results = FileReplacer.Apply([file], "*", "$1");

        Assert.Empty(results);
        Assert.Equal("hello world\n", File.ReadAllText(file));
    }

    // --- File too large ---

    [Fact]
    public void Preview_SkipsFileLargerThan10MB()
    {
        var file = Path.Combine(_tempDir, "large.txt");
        // Create a file just over 10MB
        using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write))
        {
            var line = Encoding.UTF8.GetBytes("ERROR this line repeats\n");
            long target = 10 * 1024 * 1024 + 1;
            while (fs.Length < target)
                fs.Write(line, 0, line.Length);
        }

        var previewResults = FileReplacer.Preview([file], "ERROR", "WARNING");
        Assert.Empty(previewResults);

        var applyResults = FileReplacer.Apply([file], "ERROR", "WARNING");
        Assert.Empty(applyResults);
    }

    // --- Apply IOException during write ---

    [Fact]
    public void Apply_ReportsError_WhenWriteFails()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Create file in a subdirectory, then remove write permission on the dir
        var noWriteDir = Path.Combine(_tempDir, "nowrite");
        Directory.CreateDirectory(noWriteDir);
        var file = Path.Combine(noWriteDir, "target.txt");
        File.WriteAllText(file, "ERROR here\n");

        File.SetUnixFileMode(noWriteDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var results = FileReplacer.Apply([file], "ERROR", "WARNING");
            var errors = results.Where(r => r.Error is not null).ToList();
            Assert.Single(errors);
            Assert.NotNull(errors[0].Error);
        }
        finally
        {
            File.SetUnixFileMode(noWriteDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // --- ComputeReplacements with wildcard pattern ---

    [Fact]
    public void Preview_WildcardPattern_FindsReplacements()
    {
        var file = CreateFile("wildcard_preview.txt", "start_old_end\nkeep this\nbegin_old_finish\n");
        var results = FileReplacer.Preview([file], "*old*", "$1new$2");

        Assert.Single(results);
        Assert.Equal(2, results[0].Replacements.Count);
        Assert.Equal("start_new_end", results[0].Replacements[0].ReplacedLine);
        Assert.Equal("begin_new_finish", results[0].Replacements[1].ReplacedLine);
    }

    // --- CanProcessFile IOException / UnauthorizedAccessException during binary detection ---

    [Fact]
    public void Preview_SkipsFile_WhenBinaryCheckThrowsIOException()
    {
        // A file path that exists but cannot be opened triggers IOException in CanProcessFile.
        // We simulate this by using a FIFO (named pipe) which exists and has non-zero
        // apparent length issues, but we can also simulate by removing read permission.
        if (OperatingSystem.IsWindows())
            return;

        var file = CreateFile("noread.txt", "ERROR here\n");
        File.SetUnixFileMode(file, UnixFileMode.None);

        try
        {
            var results = FileReplacer.Preview([file], "ERROR", "WARNING");
            Assert.Empty(results);
        }
        finally
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

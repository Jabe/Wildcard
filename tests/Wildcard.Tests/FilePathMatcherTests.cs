using System.Text;

namespace Wildcard.Tests;

public class FilePathMatcherTests : IDisposable
{
    private readonly string _tempDir;

    // Test files
    private readonly string _codeFile;
    private readonly string _logFile;
    private readonly string _emptyFile;
    private readonly string _noTrailingNewline;
    private readonly string _windowsLineEndings;
    private readonly string _mixedLineEndings;
    private readonly string _utf8BomFile;
    private readonly string _singleLineFile;

    public FilePathMatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wildcard_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Code file with typical C# content
        _codeFile = CreateFile("Program.cs", """
            using System;

            namespace MyApp;

            public class Program
            {
                // ERROR: This should not be here
                public static void Main(string[] args)
                {
                    Console.WriteLine("Hello, World!");
                    // TODO: Add error handling
                    throw new InvalidOperationException("ERROR: fatal crash");
                }
            }
            """);

        // Log file with various levels
        _logFile = CreateFile("app.log", """
            [2024-03-15 14:00:01] INFO    Application started successfully
            [2024-03-15 14:00:02] DEBUG   Loading configuration from appsettings.json
            [2024-03-15 14:00:03] WARN    Deprecated API endpoint called: /api/v1/users
            [2024-03-15 14:00:04] ERROR   Payment service timeout after 30s
            [2024-03-15 14:00:05] INFO    User login successful: user_id=4821
            [2024-03-15 14:00:06] ERROR   Database connection lost: retrying in 5s
            [2024-03-15 14:00:07] DEBUG   Cache hit ratio: 0.85
            [2024-03-15 14:00:08] INFO    Request processed in 42ms
            """);

        // Empty file
        _emptyFile = CreateFile("empty.txt", "");

        // File without trailing newline
        _noTrailingNewline = Path.Combine(_tempDir, "no_newline.txt");
        File.WriteAllText(_noTrailingNewline, "line one\nline two\nlast line without newline", new UTF8Encoding(false));

        // Windows line endings (\r\n)
        _windowsLineEndings = Path.Combine(_tempDir, "windows.txt");
        File.WriteAllText(_windowsLineEndings, "first line\r\nsecond ERROR line\r\nthird line\r\n", new UTF8Encoding(false));

        // Mixed line endings
        _mixedLineEndings = Path.Combine(_tempDir, "mixed.txt");
        File.WriteAllText(_mixedLineEndings, "unix line\nwindows line\r\nold mac line\rend line\n", new UTF8Encoding(false));

        // UTF-8 BOM file
        _utf8BomFile = Path.Combine(_tempDir, "bom.txt");
        File.WriteAllText(_utf8BomFile, "BOM first line\nERROR second line\n", new UTF8Encoding(true));

        // Single line file
        _singleLineFile = CreateFile("single.txt", "single ERROR line\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    // --- Helper: baseline using existing API ---
    private static List<(string FilePath, int LineNumber, string Line)> BaselineScan(
        string[] filePaths, string includePattern, string[]? excludePatterns = null)
    {
        var includeCompiled = WildcardPattern.Compile(includePattern);
        var excludeCompiled = excludePatterns?.Select(p => WildcardPattern.Compile(p)).ToArray();

        var results = new List<(string, int, string)>();
        foreach (var filePath in filePaths)
        {
            var lines = File.ReadAllLines(filePath);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!includeCompiled.IsMatch(lines[i])) continue;
                if (excludeCompiled is not null && excludeCompiled.Any(ex => ex.IsMatch(lines[i]))) continue;
                results.Add((filePath, i + 1, lines[i]));
            }
        }
        return results;
    }

    // ==================== Basic matching ====================

    [Fact]
    public void Scan_SingleIncludePattern_FindsMatchingLines()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var results = matcher.Scan(_logFile);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Contains("ERROR", r.Line));
        Assert.Equal(_logFile, results[0].FilePath);
    }

    [Fact]
    public void Scan_NoMatches_ReturnsEmptyList()
    {
        var matcher = FilePathMatcher.Create("*CRITICAL*");
        var results = matcher.Scan(_logFile);

        Assert.Empty(results);
    }

    [Fact]
    public void Scan_MatchesAllLines_ReturnsAll()
    {
        var matcher = FilePathMatcher.Create("*");
        var results = matcher.Scan(_singleLineFile);

        Assert.Single(results);
    }

    // ==================== Line numbers ====================

    [Fact]
    public void Scan_ReturnsCorrectLineNumbers()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var results = matcher.Scan(_logFile);

        // Lines 4 and 6 in the log file contain ERROR
        Assert.Equal(4, results[0].LineNumber);
        Assert.Equal(6, results[1].LineNumber);
    }

    // ==================== Include/Exclude ====================

    [Fact]
    public void Scan_WithExclude_FiltersOutExcludedLines()
    {
        var matcher = FilePathMatcher.Create(
            include: ["*ERROR*"],
            exclude: ["*timeout*"]);
        var results = matcher.Scan(_logFile);

        // Line 4 has "timeout", line 6 does not
        Assert.Single(results);
        Assert.Contains("connection lost", results[0].Line);
    }

    [Fact]
    public void Scan_MultipleIncludes_MatchesAny()
    {
        var matcher = FilePathMatcher.Create(
            include: ["*ERROR*", "*WARN*"]);
        var results = matcher.Scan(_logFile);

        Assert.Equal(3, results.Count); // 1 WARN + 2 ERROR
    }

    [Fact]
    public void Scan_ExcludeOverridesInclude()
    {
        // Include all ERROR lines, but exclude ALL of them
        var matcher = FilePathMatcher.Create(
            include: ["*ERROR*"],
            exclude: ["*ERROR*"]);
        var results = matcher.Scan(_logFile);

        Assert.Empty(results);
    }

    [Fact]
    public void Scan_MultipleExcludes_AllApplied()
    {
        var matcher = FilePathMatcher.Create(
            include: ["*ERROR*"],
            exclude: ["*timeout*", "*connection*"]);
        var results = matcher.Scan(_logFile);

        Assert.Empty(results); // Both ERROR lines excluded
    }

    // ==================== Cross-platform line endings ====================

    [Fact]
    public void Scan_WindowsLineEndings_HandledCorrectly()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var results = matcher.Scan(_windowsLineEndings);

        Assert.Single(results);
        Assert.Equal(2, results[0].LineNumber);
        Assert.DoesNotContain("\r", results[0].Line);
    }

    [Fact]
    public void Scan_NoTrailingNewline_LastLineMatched()
    {
        var matcher = FilePathMatcher.Create("*last*");
        var results = matcher.Scan(_noTrailingNewline);

        Assert.Single(results);
        Assert.Equal(3, results[0].LineNumber);
        Assert.Equal("last line without newline", results[0].Line);
    }

    [Fact]
    public void Scan_MixedLineEndings_AllLinesProcessed()
    {
        var matcher = FilePathMatcher.Create("*line*");
        var results = matcher.Scan(_mixedLineEndings);

        Assert.True(results.Count >= 3); // at least unix, windows, end
    }

    // ==================== Empty file ====================

    [Fact]
    public void Scan_EmptyFile_ReturnsEmpty()
    {
        var matcher = FilePathMatcher.Create("*");
        var results = matcher.Scan(_emptyFile);

        Assert.Empty(results);
    }

    // ==================== Multiple files ====================

    [Fact]
    public void Scan_MultipleFiles_ScansAll()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var results = matcher.Scan(_logFile, _codeFile);

        // 2 in log file + 2 in code file
        Assert.True(results.Count >= 4);
        Assert.Contains(results, r => r.FilePath == _logFile);
        Assert.Contains(results, r => r.FilePath == _codeFile);
    }

    [Fact]
    public void Scan_MultipleFiles_FilePathsCorrect()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var results = matcher.Scan(_logFile, _codeFile);

        foreach (var result in results)
        {
            Assert.True(result.FilePath == _logFile || result.FilePath == _codeFile);
        }
    }

    // ==================== UTF-8 BOM ====================

    [Fact]
    public void Scan_Utf8BomFile_FirstLineMatchedCorrectly()
    {
        var matcher = FilePathMatcher.Create("*BOM*");
        var results = matcher.Scan(_utf8BomFile);

        Assert.Single(results);
        Assert.Equal(1, results[0].LineNumber);
        // BOM should be stripped, not part of the matched content
        Assert.StartsWith("BOM", results[0].Line);
    }

    // ==================== Baseline comparison ====================

    [Fact]
    public void Scan_ProducesSameResultsAsBaseline()
    {
        var files = new[] { _logFile, _codeFile, _windowsLineEndings };
        var baseline = BaselineScan(files, "*ERROR*");

        var matcher = FilePathMatcher.Create("*ERROR*");
        var results = matcher.Scan(files);

        Assert.Equal(baseline.Count, results.Count);
        for (int i = 0; i < baseline.Count; i++)
        {
            Assert.Equal(baseline[i].Line, results[i].Line);
            Assert.Equal(baseline[i].LineNumber, results[i].LineNumber);
            Assert.Equal(baseline[i].FilePath, results[i].FilePath);
        }
    }

    [Fact]
    public void Scan_WithExcludes_ProducesSameResultsAsBaseline()
    {
        var files = new[] { _logFile };
        var baseline = BaselineScan(files, "*ERROR*", ["*timeout*"]);

        var matcher = FilePathMatcher.Create(
            include: ["*ERROR*"],
            exclude: ["*timeout*"]);
        var results = matcher.Scan(files);

        Assert.Equal(baseline.Count, results.Count);
        for (int i = 0; i < baseline.Count; i++)
        {
            Assert.Equal(baseline[i].Line, results[i].Line);
            Assert.Equal(baseline[i].LineNumber, results[i].LineNumber);
        }
    }

    // ==================== Single pattern convenience ====================

    [Fact]
    public void Create_SinglePattern_SameAsArrayOfOne()
    {
        var single = FilePathMatcher.Create("*ERROR*");
        var array = FilePathMatcher.Create(include: ["*ERROR*"]);

        var singleResults = single.Scan(_logFile);
        var arrayResults = array.Scan(_logFile);

        Assert.Equal(singleResults.Count, arrayResults.Count);
        for (int i = 0; i < singleResults.Count; i++)
        {
            Assert.Equal(singleResults[i].Line, arrayResults[i].Line);
            Assert.Equal(singleResults[i].LineNumber, arrayResults[i].LineNumber);
        }
    }

    // ==================== Async ====================

    [Fact]
    public async Task ScanAsync_ProducesSameResultsAsSync()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var syncResults = matcher.Scan(_logFile, _codeFile);

        var asyncResults = new List<FilePathMatcher.LineMatch>();
        await foreach (var match in matcher.ScanAsync([_logFile, _codeFile]))
        {
            asyncResults.Add(match);
        }

        Assert.Equal(syncResults.Count, asyncResults.Count);
        // Async may return in different order due to parallelism,
        // so compare as sets
        Assert.Equal(
            syncResults.Select(r => (r.FilePath, r.LineNumber, r.Line)).OrderBy(r => r).ToList(),
            asyncResults.Select(r => (r.FilePath, r.LineNumber, r.Line)).OrderBy(r => r).ToList());
    }

    // ==================== Multi-include OR mode ====================

    [Fact]
    public void Scan_MultipleIncludes_MatchesLineWithFirstPattern()
    {
        // *ERROR* matches lines 4 and 6 of the log file
        var matcher = FilePathMatcher.Create(include: ["*ERROR*", "*DOESNOTEXIST*"]);
        var results = matcher.Scan(_logFile);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Contains("ERROR", r.Line));
    }

    [Fact]
    public void Scan_MultipleIncludes_MatchesLineWithSecondPattern()
    {
        // *WARN* matches line 3 only; *DOESNOTEXIST* matches nothing
        var matcher = FilePathMatcher.Create(include: ["*DOESNOTEXIST*", "*WARN*"]);
        var results = matcher.Scan(_logFile);

        Assert.Single(results);
        Assert.Contains("WARN", results[0].Line);
    }

    [Fact]
    public void Scan_MultipleIncludes_LineMatchingBothPatternsAppearsOnce()
    {
        // A line containing "ERROR" also trivially matches "*" — should appear exactly once.
        // Use two patterns that both match the same ERROR lines.
        var matcher = FilePathMatcher.Create(include: ["*ERROR*", "*Payment*"]);
        var results = matcher.Scan(_logFile);

        // Line 4 contains both "ERROR" and "Payment" — must not be duplicated
        var line4Matches = results.Where(r => r.LineNumber == 4).ToList();
        Assert.Single(line4Matches);
    }

    [Fact]
    public void Scan_MultipleIncludes_NoMatchReturnsEmpty()
    {
        var matcher = FilePathMatcher.Create(include: ["*CRITICAL*", "*FATAL*"]);
        var results = matcher.Scan(_logFile);

        Assert.Empty(results);
    }

    [Fact]
    public void Scan_MultipleIncludes_WithExclude()
    {
        // Include ERROR or WARN, exclude lines with "timeout"
        var matcher = FilePathMatcher.Create(
            include: ["*ERROR*", "*WARN*"],
            exclude: ["*timeout*"]);
        var results = matcher.Scan(_logFile);

        // WARN line (3), ERROR+connection (6) — timeout ERROR line (4) excluded
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.DoesNotContain("timeout", r.Line));
    }

    [Fact]
    public void Scan_MultipleIncludes_CorrectLineNumbers()
    {
        // *WARN* = line 3, *ERROR* = lines 4, 6
        var matcher = FilePathMatcher.Create(include: ["*WARN*", "*ERROR*"]);
        var results = matcher.Scan(_logFile);

        Assert.Equal(3, results.Count);
        Assert.Equal(3, results[0].LineNumber);
        Assert.Equal(4, results[1].LineNumber);
        Assert.Equal(6, results[2].LineNumber);
    }

    [Fact]
    public void Scan_MultipleIncludes_MatchesAcrossMultipleFiles()
    {
        // WARN in log, TODO in code
        var matcher = FilePathMatcher.Create(include: ["*WARN*", "*TODO*"]);
        var results = matcher.Scan(_logFile, _codeFile);

        Assert.Contains(results, r => r.FilePath == _logFile && r.Line.Contains("WARN"));
        Assert.Contains(results, r => r.FilePath == _codeFile && r.Line.Contains("TODO"));
    }

    [Fact]
    public void Scan_MultipleIncludes_StarSuffixShapes_BytePreFilterWorks()
    {
        // Both patterns have StarSuffix shape (ASCII literals) → _multiBytePreFilterEnabled = true
        var logLineFile = CreateFile("shapes_test.txt",
            "payment.log opened\nconfig.json loaded\nreport.csv exported\ndata.xml parsed\n");

        var matcher = FilePathMatcher.Create(include: ["*.log*", "*.csv*"]);
        var results = matcher.Scan(logLineFile);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Line.Contains(".log"));
        Assert.Contains(results, r => r.Line.Contains(".csv"));
    }

    [Fact]
    public void Scan_MultipleIncludes_PrefixStarShapes_BytePreFilterWorks()
    {
        // Both patterns have PrefixStar shape (ASCII literals) → byte pre-filter active
        var file = CreateFile("prefix_test.txt",
            "ERROR: something failed\nWARN: low memory\nINFO: all good\nDEBUG: verbose\n");

        var matcher = FilePathMatcher.Create(include: ["ERROR*", "WARN*"]);
        var results = matcher.Scan(file);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Line.StartsWith("ERROR"));
        Assert.Contains(results, r => r.Line.StartsWith("WARN"));
    }

    [Fact]
    public void Scan_MultipleIncludes_StarContainsStarShapes_BytePreFilterWorks()
    {
        // Both patterns are StarContainsStar — byte pre-filter checks IndexOf
        var matcher = FilePathMatcher.Create(include: ["*ERROR*", "*WARN*"]);
        var results = matcher.Scan(_logFile);

        // Verify correct count; byte pre-filter must not drop valid lines
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Scan_MultipleIncludes_NonAsciiPattern_StillMatchesCorrectly()
    {
        // Non-ASCII pattern disables byte pre-filter for that pattern
        // (_multiBytePreFilterEnabled = false), but matching must still work
        var file = CreateFile("unicode_test.txt",
            "hello world\ncafé au lait\nerror occurred\n");

        var matcher = FilePathMatcher.Create(include: ["*café*", "*error*"]);
        var results = matcher.Scan(file);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Scan_MultipleIncludes_MatchesOnWindows_LineEndings()
    {
        var matcher = FilePathMatcher.Create(include: ["*ERROR*", "*INFO*"]);
        var results = matcher.Scan(_windowsLineEndings);

        // windows.txt has "second ERROR line" → 1 match
        Assert.Single(results);
        Assert.DoesNotContain("\r", results[0].Line);
    }

    [Fact]
    public void ContainsMatch_MultipleIncludes_ReturnsTrueWhenAnyMatches()
    {
        var matcher = FilePathMatcher.Create(include: ["*CRITICAL*", "*WARN*"]);
        Assert.True(matcher.ContainsMatch(_logFile));  // log has WARN
    }

    [Fact]
    public void ContainsMatch_MultipleIncludes_ReturnsFalseWhenNoneMatch()
    {
        var matcher = FilePathMatcher.Create(include: ["*CRITICAL*", "*FATAL*"]);
        Assert.False(matcher.ContainsMatch(_logFile));
    }

    [Fact]
    public void Scan_MultipleIncludes_ResultsMatchOrOfSinglePatterns()
    {
        // Verify OR semantics: multi-include should equal union of individual scans (deduped, ordered)
        var m1 = FilePathMatcher.Create("*ERROR*").Scan(_logFile).Select(r => r.LineNumber).ToHashSet();
        var m2 = FilePathMatcher.Create("*INFO*").Scan(_logFile).Select(r => r.LineNumber).ToHashSet();
        var expected = m1.Union(m2).OrderBy(n => n).ToList();

        var combined = FilePathMatcher.Create(include: ["*ERROR*", "*INFO*"]).Scan(_logFile);
        var actual = combined.Select(r => r.LineNumber).OrderBy(n => n).ToList();

        Assert.Equal(expected, actual);
    }
}

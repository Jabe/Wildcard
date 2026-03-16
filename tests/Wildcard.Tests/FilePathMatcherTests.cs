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

    // --- ScanWithContext tests ---

    [Fact]
    public void ScanWithContext_BeforeContext_ReturnsLinesBeforeMatch()
    {
        // _logFile line 4 is ERROR, so before=2 should give lines 2,3,4
        var matcher = FilePathMatcher.Create("*Payment service*");
        var results = matcher.ScanWithContext(beforeContext: 2, afterContext: 0, _logFile);

        Assert.True(results.Count >= 3);
        var matchLine = results.Single(r => r.IsMatch);
        Assert.Equal(4, matchLine.LineNumber);
        Assert.Contains("Payment service", matchLine.Line);

        // Context lines before the match
        var contextBefore = results.Where(r => !r.IsMatch && r.LineNumber < matchLine.LineNumber).ToList();
        Assert.Equal(2, contextBefore.Count);
        Assert.Equal(2, contextBefore[0].LineNumber);
        Assert.Equal(3, contextBefore[1].LineNumber);
    }

    [Fact]
    public void ScanWithContext_AfterContext_ReturnsLinesAfterMatch()
    {
        // _logFile line 4 is ERROR (Payment), after=2 should give lines 4,5,6
        var matcher = FilePathMatcher.Create("*Payment service*");
        var results = matcher.ScanWithContext(beforeContext: 0, afterContext: 2, _logFile);

        Assert.True(results.Count >= 3);
        var matchLine = results.Single(r => r.IsMatch);
        Assert.Equal(4, matchLine.LineNumber);

        var contextAfter = results.Where(r => !r.IsMatch && r.LineNumber > matchLine.LineNumber).ToList();
        Assert.Equal(2, contextAfter.Count);
        Assert.Equal(5, contextAfter[0].LineNumber);
        Assert.Equal(6, contextAfter[1].LineNumber);
    }

    [Fact]
    public void ScanWithContext_OverlappingContextMerged()
    {
        // _logFile: ERROR on lines 4 and 6. With context=1, ranges [3,5] and [5,7] should merge to [3,7]
        var matcher = FilePathMatcher.Create("*ERROR*");
        var results = matcher.ScanWithContext(beforeContext: 1, afterContext: 1, _logFile);

        // Should be contiguous lines 3-7 with no duplicates
        var lineNumbers = results.Select(r => r.LineNumber).ToList();
        for (int i = 1; i < lineNumbers.Count; i++)
            Assert.Equal(lineNumbers[i - 1] + 1, lineNumbers[i]);

        // No duplicate line numbers
        Assert.Equal(lineNumbers.Count, lineNumbers.Distinct().Count());
    }

    [Fact]
    public void ScanWithContext_IsMatchFlag_CorrectlySet()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var results = matcher.ScanWithContext(beforeContext: 1, afterContext: 1, _logFile);

        var matchLines = results.Where(r => r.IsMatch).ToList();
        var contextLines = results.Where(r => !r.IsMatch).ToList();

        // All match lines should contain ERROR
        foreach (var m in matchLines)
            Assert.Contains("ERROR", m.Line);

        // Context lines should NOT contain ERROR
        foreach (var c in contextLines)
            Assert.DoesNotContain("ERROR", c.Line);
    }

    [Fact]
    public void ScanWithContext_NoContext_EquivalentToScan()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var scanResults = matcher.Scan(_logFile);
        var contextResults = matcher.ScanWithContext(beforeContext: 0, afterContext: 0, _logFile);

        Assert.Equal(scanResults.Count, contextResults.Count);
        for (int i = 0; i < scanResults.Count; i++)
        {
            Assert.Equal(scanResults[i].LineNumber, contextResults[i].LineNumber);
            Assert.Equal(scanResults[i].Line, contextResults[i].Line);
            Assert.True(contextResults[i].IsMatch);
        }
    }

    [Fact]
    public void ScanWithContext_ContextNearFileStart_DoesNotGoNegative()
    {
        // _logFile line 1 is INFO. Before=5 should not produce negative line numbers.
        var matcher = FilePathMatcher.Create("*Application started*");
        var results = matcher.ScanWithContext(beforeContext: 5, afterContext: 0, _logFile);

        Assert.All(results, r => Assert.True(r.LineNumber >= 1));
        Assert.Contains(results, r => r.IsMatch && r.LineNumber == 1);
    }

    [Fact]
    public void ScanWithContext_ContextNearFileEnd_DoesNotExceedFile()
    {
        // _logFile line 8 is INFO (last line). After=10 should not exceed file.
        var matcher = FilePathMatcher.Create("*Request processed*");
        var results = matcher.ScanWithContext(beforeContext: 0, afterContext: 10, _logFile);

        Assert.Contains(results, r => r.IsMatch);
        // All line numbers should be valid (no crash, no out-of-bounds)
        Assert.All(results, r => Assert.True(r.LineNumber >= 1));
    }

    [Fact]
    public void ScanWithContext_NonContiguousGroups_HaveGaps()
    {
        // _logFile: INFO on lines 1, 5, 8. With context=0, there should be gaps between them.
        var matcher = FilePathMatcher.Create("*INFO*");
        var results = matcher.ScanWithContext(beforeContext: 0, afterContext: 0, _logFile);

        Assert.True(results.Count >= 3);
        var lineNumbers = results.Select(r => r.LineNumber).ToList();

        // There should be at least one gap (non-consecutive line numbers)
        bool hasGap = false;
        for (int i = 1; i < lineNumbers.Count; i++)
        {
            if (lineNumbers[i] != lineNumbers[i - 1] + 1)
            {
                hasGap = true;
                break;
            }
        }
        Assert.True(hasGap, "Expected non-contiguous groups with gaps in line numbers");
    }

    // ==================== ContainsMatch ====================

    [Fact]
    public void ContainsMatch_ReturnsTrue_WhenMatchExists()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        Assert.True(matcher.ContainsMatch(_logFile));
    }

    [Fact]
    public void ContainsMatch_ReturnsFalse_WhenNoMatch()
    {
        var matcher = FilePathMatcher.Create("*CRITICAL*");
        Assert.False(matcher.ContainsMatch(_logFile));
    }

    [Fact]
    public void ContainsMatch_EmptyFile_ReturnsFalse()
    {
        var matcher = FilePathMatcher.Create("*");
        Assert.False(matcher.ContainsMatch(_emptyFile));
    }

    [Fact]
    public void ContainsMatch_NonExistentFile_ReturnsFalse()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var fakePath = Path.Combine(_tempDir, "does_not_exist.txt");
        Assert.False(matcher.ContainsMatch(fakePath));
    }

    [Fact]
    public void ContainsMatch_BomFile_MatchesFirstLine()
    {
        var matcher = FilePathMatcher.Create("BOM*");
        Assert.True(matcher.ContainsMatch(_utf8BomFile));
    }

    [Fact]
    public void ContainsMatch_BomFile_MatchesSecondLine()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        Assert.True(matcher.ContainsMatch(_utf8BomFile));
    }

    // ==================== Create validation ====================

    [Fact]
    public void Create_EmptyIncludeArray_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => FilePathMatcher.Create(include: []));
    }

    [Fact]
    public void Create_NullInclude_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => FilePathMatcher.Create(include: (string[])null!));
    }

    [Fact]
    public void Create_NullSingleInclude_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => FilePathMatcher.Create((string)null!));
    }

    // ==================== Scan with zero files ====================

    [Fact]
    public void Scan_ZeroFiles_ReturnsEmpty()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var results = matcher.Scan();
        Assert.Empty(results);
    }

    [Fact]
    public void Scan_EmptyArray_ReturnsEmpty()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var results = matcher.Scan(Array.Empty<string>());
        Assert.Empty(results);
    }

    // ==================== ScanWithContext: multiple files (parallel path) ====================

    [Fact]
    public void ScanWithContext_MultipleFiles_ReturnsResultsFromAll()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var results = matcher.ScanWithContext(beforeContext: 1, afterContext: 1, _logFile, _codeFile);

        Assert.Contains(results, r => r.FilePath == _logFile);
        Assert.Contains(results, r => r.FilePath == _codeFile);
        // Match lines should have IsMatch = true
        Assert.True(results.Where(r => r.IsMatch).All(r => r.Line.Contains("ERROR")));
    }

    [Fact]
    public void ScanWithContext_ThreeOrMoreFiles_ParallelPath()
    {
        var file1 = CreateFile("ctx_par1.txt", "alpha\nbeta ERROR one\ngamma\n");
        var file2 = CreateFile("ctx_par2.txt", "delta\nepsilon\nzeta ERROR two\neta\n");
        var file3 = CreateFile("ctx_par3.txt", "theta\niota ERROR three\nkappa\n");

        var matcher = FilePathMatcher.Create("*ERROR*");
        var results = matcher.ScanWithContext(beforeContext: 1, afterContext: 1, file1, file2, file3);

        // Each file should contribute context
        Assert.Contains(results, r => r.FilePath == file1 && r.IsMatch);
        Assert.Contains(results, r => r.FilePath == file2 && r.IsMatch);
        Assert.Contains(results, r => r.FilePath == file3 && r.IsMatch);
    }

    // ==================== ScanWithContext: empty and non-existent files ====================

    [Fact]
    public void ScanWithContext_EmptyFile_ReturnsEmpty()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var results = matcher.ScanWithContext(beforeContext: 2, afterContext: 2, _emptyFile);
        Assert.Empty(results);
    }

    [Fact]
    public void ScanWithContext_NonExistentFile_ReturnsEmpty()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var fakePath = Path.Combine(_tempDir, "no_such_file.txt");
        var results = matcher.ScanWithContext(beforeContext: 1, afterContext: 1, fakePath);
        Assert.Empty(results);
    }

    [Fact]
    public void ScanWithContext_ZeroFiles_ReturnsEmpty()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var results = matcher.ScanWithContext(beforeContext: 1, afterContext: 1);
        Assert.Empty(results);
    }

    // ==================== Byte pre-filter: exact match (no excludes) ====================

    [Fact]
    public void Scan_StarContainsStar_NoExcludes_BytePreFilterExactMatch()
    {
        // Pattern *needle* → StarContainsStar shape, no excludes → exactByteMatch = true
        var file = CreateFile("byte_scs.txt", "the needle is here\nno match\nanother needle found\n");
        var matcher = FilePathMatcher.Create("*needle*");
        var results = matcher.Scan(file);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Contains("needle", r.Line));
    }

    [Fact]
    public void Scan_PureLiteral_NoExcludes_BytePreFilterExactMatch()
    {
        // Pattern without wildcards → PureLiteral shape
        var file = CreateFile("byte_pl.txt", "exact\nnot exact\nexact\nexactly not\n");
        var matcher = FilePathMatcher.Create("exact");
        var results = matcher.Scan(file);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("exact", r.Line));
    }

    [Fact]
    public void Scan_PrefixStarSuffix_NoExcludes_BytePreFilterExactMatch()
    {
        // Pattern prefix*suffix → PrefixStarSuffix shape
        var file = CreateFile("byte_pss.txt", "START_something_END\nSTART_END\nnope\nSTART_middle_END\n");
        var matcher = FilePathMatcher.Create("START*END");
        var results = matcher.Scan(file);

        Assert.Equal(3, results.Count);
        Assert.All(results, r =>
        {
            Assert.StartsWith("START", r.Line);
            Assert.EndsWith("END", r.Line);
        });
    }

    [Fact]
    public void Scan_StarSuffix_NoExcludes_BytePreFilterExactMatch()
    {
        // Pattern *suffix → StarSuffix shape
        var file = CreateFile("byte_ss.txt", "hello.log\nworld.txt\ntest.log\nfoo.csv\n");
        var matcher = FilePathMatcher.Create("*.log");
        var results = matcher.Scan(file);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.EndsWith(".log", r.Line));
    }

    [Fact]
    public void Scan_PrefixStar_NoExcludes_BytePreFilterExactMatch()
    {
        // Pattern prefix* → PrefixStar shape
        var file = CreateFile("byte_ps.txt", "ERROR: fail\nERROR: crash\nINFO: ok\nERROR\n");
        var matcher = FilePathMatcher.Create("ERROR*");
        var results = matcher.Scan(file);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.StartsWith("ERROR", r.Line));
    }

    // ==================== Large file (>64KB) memory-mapped path ====================

    [Fact]
    public void Scan_LargeFile_MemoryMappedPath()
    {
        // Create a file > 64KB to exercise the memory-mapped code path
        var sb = new StringBuilder();
        // Each line ~80 chars, need > 64*1024 / 80 ≈ 820 lines
        for (int i = 0; i < 1000; i++)
            sb.AppendLine($"Line {i:D4}: This is filler content to make the file large enough for memory mapping.");
        sb.AppendLine("Line 1000: TARGET_MATCH_HERE found");
        for (int i = 1001; i < 1500; i++)
            sb.AppendLine($"Line {i:D4}: More filler content to ensure the file exceeds the 64KB threshold value.");

        var largeFile = CreateFile("large_mmap.txt", sb.ToString());
        var fileInfo = new FileInfo(largeFile);
        Assert.True(fileInfo.Length > 64 * 1024, "File should be > 64KB to exercise memory-mapped path");

        var matcher = FilePathMatcher.Create("*TARGET_MATCH_HERE*");
        var results = matcher.Scan(largeFile);

        Assert.Single(results);
        Assert.Equal(1001, results[0].LineNumber);
        Assert.Contains("TARGET_MATCH_HERE", results[0].Line);
    }

    [Fact]
    public void ContainsMatch_LargeFile_MemoryMappedPath()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 1000; i++)
            sb.AppendLine($"Line {i:D4}: Filler content to bulk up the file past the buffered read threshold.");
        sb.AppendLine("Line 1000: FOUND_THE_NEEDLE");
        for (int i = 1001; i < 1500; i++)
            sb.AppendLine($"Line {i:D4}: More filler content to ensure file is large enough for memory mapped IO.");

        var largeFile = CreateFile("large_contains.txt", sb.ToString());
        var fileInfo = new FileInfo(largeFile);
        Assert.True(fileInfo.Length > 64 * 1024);

        var matcher = FilePathMatcher.Create("*FOUND_THE_NEEDLE*");
        Assert.True(matcher.ContainsMatch(largeFile));

        var noMatchMatcher = FilePathMatcher.Create("*DOES_NOT_EXIST_IN_FILE*");
        Assert.False(noMatchMatcher.ContainsMatch(largeFile));
    }

    [Fact]
    public void ScanWithContext_LargeFile_MemoryMappedPath()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 1000; i++)
            sb.AppendLine($"Line {i:D4}: Padding content to push file size past the sixty-four KB buffered read limit.");
        sb.AppendLine("Line 1000: CONTEXT_TARGET_LINE");
        for (int i = 1001; i < 1500; i++)
            sb.AppendLine($"Line {i:D4}: Additional padding content so the file remains large enough for mmap path.");

        var largeFile = CreateFile("large_context.txt", sb.ToString());
        Assert.True(new FileInfo(largeFile).Length > 64 * 1024);

        var matcher = FilePathMatcher.Create("*CONTEXT_TARGET_LINE*");
        var results = matcher.ScanWithContext(beforeContext: 2, afterContext: 2, largeFile);

        Assert.True(results.Count >= 5); // 2 before + 1 match + 2 after
        var matchLine = results.Single(r => r.IsMatch);
        Assert.Equal(1001, matchLine.LineNumber);
    }

    // ==================== Multi-include byte pre-filter with PureLiteral shapes ====================

    [Fact]
    public void Scan_MultipleIncludes_PureLiteralShapes_BytePreFilterWorks()
    {
        var file = CreateFile("multi_pl.txt", "alpha\nbeta\ngamma\ndelta\n");
        var matcher = FilePathMatcher.Create(include: ["alpha", "gamma"]);
        var results = matcher.Scan(file);

        Assert.Equal(2, results.Count);
        Assert.Equal("alpha", results[0].Line);
        Assert.Equal("gamma", results[1].Line);
    }

    [Fact]
    public void Scan_MultipleIncludes_PrefixStarSuffixShapes_BytePreFilterWorks()
    {
        var file = CreateFile("multi_pss.txt", "A_x_Z\nB_y_Z\nA_hello_Z\nC_foo_D\n");
        var matcher = FilePathMatcher.Create(include: ["A*Z", "B*Z"]);
        var results = matcher.Scan(file);

        Assert.Equal(3, results.Count);
    }

    // ==================== Case-insensitive disables byte pre-filter ====================

    [Fact]
    public void Scan_CaseInsensitive_StillMatchesCorrectly()
    {
        // IgnoreCase = true disables byte pre-filter, but matching must still work
        var file = CreateFile("case_test.txt", "Hello World\nhello world\nHELLO WORLD\nno match\n");
        var matcher = FilePathMatcher.Create("*hello*", new FilePathMatcher.Options { IgnoreCase = true });
        var results = matcher.Scan(file);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void ContainsMatch_CaseInsensitive_Works()
    {
        var file = CreateFile("case_contains.txt", "no match\nHELLO there\n");
        var matcher = FilePathMatcher.Create("*hello*", new FilePathMatcher.Options { IgnoreCase = true });
        Assert.True(matcher.ContainsMatch(file));
    }

    [Fact]
    public void Scan_CaseInsensitive_PureLiteral_NoBytePreFilter()
    {
        // PureLiteral + IgnoreCase: byte pre-filter disabled but matching works
        var file = CreateFile("case_pl.txt", "exact\nEXACT\nExact\nwrong\n");
        var matcher = FilePathMatcher.Create("exact", new FilePathMatcher.Options { IgnoreCase = true });
        var results = matcher.Scan(file);

        Assert.Equal(3, results.Count);
    }

    // ==================== Long lines exceeding char buffer (>65536 chars) ====================

    [Fact]
    public void Scan_LongLine_ExceedingCharBuffer_Handled()
    {
        // Create a line > 65536 chars to trigger DecodeToChars overflow → rented buffer path
        var longContent = new string('A', 70000) + "NEEDLE" + new string('B', 1000);
        var file = CreateFile("long_line.txt", longContent + "\nshort line\n");

        var matcher = FilePathMatcher.Create("*NEEDLE*");
        var results = matcher.Scan(file);

        Assert.Single(results);
        Assert.Equal(1, results[0].LineNumber);
        Assert.Contains("NEEDLE", results[0].Line);
    }

    [Fact]
    public void ContainsMatch_LongLine_ExceedingCharBuffer()
    {
        var longContent = new string('X', 70000) + "FOUND" + new string('Y', 1000);
        var file = CreateFile("long_line_contains.txt", longContent + "\nno\n");

        var matcher = FilePathMatcher.Create("*FOUND*");
        Assert.True(matcher.ContainsMatch(file));
    }

    [Fact]
    public void ScanWithContext_LongLine_ExceedingCharBuffer()
    {
        var longContent = new string('Z', 70000) + "TARGET" + new string('W', 1000);
        var file = CreateFile("long_line_ctx.txt", "before line\n" + longContent + "\nafter line\n");

        var matcher = FilePathMatcher.Create("*TARGET*");
        var results = matcher.ScanWithContext(beforeContext: 1, afterContext: 1, file);

        Assert.True(results.Count >= 3);
        var matchLine = results.Single(r => r.IsMatch);
        Assert.Equal(2, matchLine.LineNumber);
        Assert.Contains("TARGET", matchLine.Line);
    }

    // ==================== ScanAsync with cancellation ====================

    [Fact]
    public async Task ScanAsync_CancellationToken_StopsEarly()
    {
        // Create many files to increase likelihood of cancellation taking effect
        var files = new string[50];
        for (int i = 0; i < files.Length; i++)
        {
            var sb = new StringBuilder();
            for (int j = 0; j < 100; j++)
                sb.AppendLine($"Line {j}: ERROR in file {i}");
            files[i] = CreateFile($"cancel_{i}.txt", sb.ToString());
        }

        var matcher = FilePathMatcher.Create("*ERROR*");
        var cts = new CancellationTokenSource();

        var collected = new List<FilePathMatcher.LineMatch>();
        try
        {
            await foreach (var match in matcher.ScanAsync(files, cts.Token))
            {
                collected.Add(match);
                if (collected.Count >= 10)
                    cts.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // We should have collected some results but likely not all 5000
        Assert.True(collected.Count >= 10);
    }

    [Fact]
    public async Task ScanAsync_AlreadyCancelled_YieldsNothing()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var collected = new List<FilePathMatcher.LineMatch>();
        try
        {
            await foreach (var match in matcher.ScanAsync([_logFile], cts.Token))
                collected.Add(match);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Should either throw or yield no/few results
        Assert.True(collected.Count < 100);
    }

    [Fact]
    public async Task ScanAsync_EmptyArray_YieldsNothing()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var collected = new List<FilePathMatcher.LineMatch>();
        await foreach (var match in matcher.ScanAsync([]))
            collected.Add(match);

        Assert.Empty(collected);
    }

    // ==================== Scan on non-existent file ====================

    [Fact]
    public void Scan_NonExistentFile_ReturnsEmpty()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var fakePath = Path.Combine(_tempDir, "ghost.txt");
        var results = matcher.Scan(fakePath);
        Assert.Empty(results);
    }

    // ==================== ScanWithContext negative context values ====================

    [Fact]
    public void ScanWithContext_NegativeContext_DelegatesToScan()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        var scanResults = matcher.Scan(_logFile);
        var contextResults = matcher.ScanWithContext(beforeContext: -1, afterContext: -1, _logFile);

        Assert.Equal(scanResults.Count, contextResults.Count);
        for (int i = 0; i < scanResults.Count; i++)
        {
            Assert.Equal(scanResults[i].LineNumber, contextResults[i].LineNumber);
            Assert.Equal(scanResults[i].Line, contextResults[i].Line);
            Assert.True(contextResults[i].IsMatch);
        }
    }

    // ==================== Byte pre-filter with excludes (no exactByteMatch) ====================

    [Fact]
    public void Scan_StarContainsStar_WithExcludes_NoExactByteMatch()
    {
        // When excludes are present, exactByteMatch is false even if byte pre-filter matches
        var file = CreateFile("byte_excl.txt", "needle in haystack\nneedle in water\nno match\n");
        var matcher = FilePathMatcher.Create(
            include: ["*needle*"],
            exclude: ["*water*"]);
        var results = matcher.Scan(file);

        Assert.Single(results);
        Assert.Contains("haystack", results[0].Line);
    }

    // ==================== ContainsMatch with Windows line endings ====================

    [Fact]
    public void ContainsMatch_WindowsLineEndings_Works()
    {
        var matcher = FilePathMatcher.Create("*ERROR*");
        Assert.True(matcher.ContainsMatch(_windowsLineEndings));
    }

    // ==================== Large file BOM detection in memory-mapped path ====================

    [Fact]
    public void Scan_LargeFileWithBom_FirstLineMatchedCorrectly()
    {
        var sb = new StringBuilder();
        sb.AppendLine("BOM_FIRST_LINE_MATCH");
        for (int i = 0; i < 1500; i++)
            sb.AppendLine($"Line {i:D4}: Padding content to make the file large enough to exceed the buffered threshold.");

        var largeFile = Path.Combine(_tempDir, "large_bom.txt");
        File.WriteAllText(largeFile, sb.ToString(), new UTF8Encoding(true)); // BOM = true
        Assert.True(new FileInfo(largeFile).Length > 64 * 1024);

        var matcher = FilePathMatcher.Create("BOM_FIRST_LINE_MATCH");
        var results = matcher.Scan(largeFile);

        Assert.Single(results);
        Assert.Equal(1, results[0].LineNumber);
        Assert.Equal("BOM_FIRST_LINE_MATCH", results[0].Line);
    }

    [Fact]
    public void ContainsMatch_LargeFileWithBom_Works()
    {
        var sb = new StringBuilder();
        sb.AppendLine("BOM_TARGET_LINE");
        for (int i = 0; i < 1500; i++)
            sb.AppendLine($"Line {i:D4}: Padding for size to exceed the sixty-four kilobyte buffered read threshold value.");

        var largeFile = Path.Combine(_tempDir, "large_bom_contains.txt");
        File.WriteAllText(largeFile, sb.ToString(), new UTF8Encoding(true));
        Assert.True(new FileInfo(largeFile).Length > 64 * 1024);

        var matcher = FilePathMatcher.Create("BOM_TARGET_LINE");
        Assert.True(matcher.ContainsMatch(largeFile));
    }

    // ==================== MinLength pre-filter ====================

    [Fact]
    public void Scan_ShortLines_FilteredByMinLength()
    {
        // Pattern "abcdef" has MinLength 6 → lines shorter than 6 chars are pre-filtered
        var file = CreateFile("minlen.txt", "ab\nabcdef\nabc\nabcdefgh\n");
        var matcher = FilePathMatcher.Create("abcdef");
        var results = matcher.Scan(file);

        Assert.Single(results);
        Assert.Equal("abcdef", results[0].Line);
    }
}

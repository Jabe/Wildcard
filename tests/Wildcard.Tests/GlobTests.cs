namespace Wildcard.Tests;

public class GlobTests : IDisposable
{
    private readonly string _tempDir;

    public GlobTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wildcard_glob_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Build a directory tree:
        // src/
        //   Program.cs
        //   Lib.cs
        //   utils/
        //     Helper.cs
        //     data.json
        //   deep/
        //     nested/
        //       File.cs
        // docs/
        //   readme.md
        // root.txt

        CreateFile("src/Program.cs");
        CreateFile("src/Lib.cs");
        CreateFile("src/utils/Helper.cs");
        CreateFile("src/utils/data.json");
        CreateFile("src/deep/nested/File.cs");
        CreateFile("docs/readme.md");
        CreateFile("root.txt");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void CreateFile(string relativePath)
    {
        var fullPath = Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, $"content of {relativePath}");
    }

    private List<string> Glob(string pattern) =>
        Wildcard.Glob.Match(pattern, _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // --- Literal paths ---

    [Fact]
    public void Literal_ExactFile()
    {
        var results = Glob("root.txt");
        Assert.Equal(["root.txt"], results);
    }

    [Fact]
    public void Literal_NestedPath()
    {
        var results = Glob("src/Program.cs");
        Assert.Equal(["src/Program.cs"], results);
    }

    [Fact]
    public void Literal_NonExistent_ReturnsEmpty()
    {
        var results = Glob("nope.txt");
        Assert.Empty(results);
    }

    // --- Single star ---

    [Fact]
    public void Star_MatchesFilesInRoot()
    {
        var results = Glob("*.txt");
        Assert.Equal(["root.txt"], results);
    }

    [Fact]
    public void Star_MatchesFilesInSubdirectory()
    {
        var results = Glob("src/*.cs");
        Assert.Equal(["src/Lib.cs", "src/Program.cs"], results);
    }

    [Fact]
    public void Star_DoesNotRecurse()
    {
        var results = Glob("*.cs");
        Assert.Empty(results); // no .cs files in root
    }

    [Fact]
    public void Star_MatchesDirectorySegment()
    {
        // src/*/Helper.cs — should NOT match because * doesn't recurse into subdirs of utils
        // It matches: src/<any-dir>/Helper.cs
        var results = Glob("src/*/Helper.cs");
        Assert.Equal(["src/utils/Helper.cs"], results);
    }

    // --- Double star ---

    [Fact]
    public void DoubleStar_MatchesAllCsFiles()
    {
        var results = Glob("**/*.cs");
        Assert.Equal([
            "src/deep/nested/File.cs",
            "src/Lib.cs",
            "src/Program.cs",
            "src/utils/Helper.cs",
        ], results);
    }

    [Fact]
    public void DoubleStar_AtStart_MatchesAnyDepth()
    {
        var results = Glob("**/File.cs");
        Assert.Equal(["src/deep/nested/File.cs"], results);
    }

    [Fact]
    public void DoubleStar_InMiddle()
    {
        var results = Glob("src/**/*.cs");
        Assert.Equal([
            "src/deep/nested/File.cs",
            "src/Lib.cs",
            "src/Program.cs",
            "src/utils/Helper.cs",
        ], results);
    }

    [Fact]
    public void DoubleStar_MatchesZeroLevels()
    {
        // src/**/*.cs should match src/Program.cs (zero intermediate dirs)
        var results = Glob("src/**/*.cs");
        Assert.Contains("src/Program.cs", results);
    }

    [Fact]
    public void DoubleStar_MatchesMultipleLevels()
    {
        var results = Glob("src/**/*.cs");
        Assert.Contains("src/deep/nested/File.cs", results);
    }

    // --- Character classes ---

    [Fact]
    public void CharClass_InFilename()
    {
        var results = Glob("src/[PL]*.cs");
        Assert.Equal(["src/Lib.cs", "src/Program.cs"], results);
    }

    // --- Question mark ---

    [Fact]
    public void QuestionMark_SingleChar()
    {
        var results = Glob("src/??b.cs");
        Assert.Equal(["src/Lib.cs"], results);
    }

    // --- Edge cases ---

    [Fact]
    public void EmptyResults()
    {
        var results = Glob("**/*.xyz");
        Assert.Empty(results);
    }

    [Fact]
    public void ConsecutiveDoubleStars_Collapsed()
    {
        var results = Glob("**/**/*.cs");
        // Should behave same as **/*.cs
        var expected = Glob("**/*.cs");
        Assert.Equal(expected, results);
    }

    [Fact]
    public void BackslashSeparator_Normalized()
    {
        var results = Glob("src\\*.cs");
        Assert.Equal(["src/Lib.cs", "src/Program.cs"], results);
    }

    [Fact]
    public void Parse_NullPattern_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Wildcard.Glob.Parse(null!));
    }

    [Fact]
    public void MixedExtensions()
    {
        var results = Glob("src/utils/*");
        Assert.Equal(["src/utils/data.json", "src/utils/Helper.cs"], results);
    }

    [Fact]
    public void AllFilesRecursive()
    {
        var results = Glob("**/*");
        Assert.Equal(7, results.Count); // all 7 files
    }

    [Fact]
    public void DoubleStar_Only_MatchesAllFiles()
    {
        var results = Glob("**");
        Assert.Equal(7, results.Count);
    }

    [Fact]
    public void DoubleStar_UnderDirectory_MatchesAllFiles()
    {
        var results = Glob("src/**");
        Assert.Contains("src/Program.cs", results);
        Assert.Contains("src/Lib.cs", results);
        Assert.Contains("src/utils/Helper.cs", results);
        Assert.Contains("src/utils/data.json", results);
        Assert.Contains("src/deep/nested/File.cs", results);
        Assert.Equal(5, results.Count);
    }

    // --- Absolute paths ---

    [Fact]
    public void AbsolutePath_LiteralFile()
    {
        var absPattern = Path.Combine(_tempDir, "root.txt").Replace('\\', '/');
        var results = Wildcard.Glob.Match(absPattern).ToList();
        Assert.Single(results);
        Assert.Equal(Path.Combine(_tempDir, "root.txt"), results[0]);
    }

    [Fact]
    public void AbsolutePath_WithWildcard()
    {
        var absPattern = Path.Combine(_tempDir, "src", "*.cs").Replace('\\', '/');
        var results = Wildcard.Glob.Match(absPattern)
            .Select(p => Path.GetFileName(p))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(["Lib.cs", "Program.cs"], results);
    }

    [Fact]
    public void AbsolutePath_WithDoubleStar()
    {
        var absPattern = Path.Combine(_tempDir, "**", "*.cs").Replace('\\', '/');
        var results = Wildcard.Glob.Match(absPattern).ToList();
        Assert.Equal(4, results.Count);
    }

    [Fact]
    public void AbsolutePath_IgnoresBaseDirectory()
    {
        // When pattern is absolute, baseDirectory should be ignored
        var absPattern = Path.Combine(_tempDir, "root.txt").Replace('\\', '/');
        var results = Wildcard.Glob.Parse(absPattern).EnumerateMatches("/nonexistent").ToList();
        Assert.Single(results);
    }

    // --- IsMatch (path matching without filesystem) ---

    [Theory]
    [InlineData("*.cs", "Program.cs", true)]
    [InlineData("*.cs", "readme.md", false)]
    [InlineData("*.cs", "src/Program.cs", false)] // * does not cross /
    [InlineData("src/*.cs", "src/Program.cs", true)]
    [InlineData("src/*.cs", "src/deep/File.cs", false)]
    [InlineData("src/*.cs", "docs/readme.md", false)]
    public void IsMatch_SingleStar(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Wildcard.Glob.IsMatch(pattern, path));
    }

    [Theory]
    [InlineData("**/*.cs", "Program.cs", true)]
    [InlineData("**/*.cs", "src/Program.cs", true)]
    [InlineData("**/*.cs", "src/deep/nested/File.cs", true)]
    [InlineData("**/*.cs", "readme.md", false)]
    [InlineData("src/**/*.cs", "src/Program.cs", true)]
    [InlineData("src/**/*.cs", "src/deep/nested/File.cs", true)]
    [InlineData("src/**/*.cs", "docs/readme.md", false)]
    [InlineData("**", "any/path/at/all.txt", true)]
    [InlineData("**", "file.txt", true)]
    public void IsMatch_DoubleStar(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Wildcard.Glob.IsMatch(pattern, path));
    }

    [Theory]
    [InlineData("src/[PL]*.cs", "src/Program.cs", true)]
    [InlineData("src/[PL]*.cs", "src/Lib.cs", true)]
    [InlineData("src/[PL]*.cs", "src/data.json", false)]
    public void IsMatch_CharacterClass(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Wildcard.Glob.IsMatch(pattern, path));
    }

    [Theory]
    [InlineData("src/??b.cs", "src/Lib.cs", true)]
    [InlineData("src/??b.cs", "src/Program.cs", false)]
    public void IsMatch_QuestionMark(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Wildcard.Glob.IsMatch(pattern, path));
    }

    [Theory]
    [InlineData("src\\*.cs", "src/Program.cs", true)]
    [InlineData("src/*.cs", "src\\Program.cs", true)]
    public void IsMatch_NormalizesBackslashes(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Wildcard.Glob.IsMatch(pattern, path));
    }

    [Fact]
    public void IsMatch_ExactLiteral()
    {
        Assert.True(Wildcard.Glob.IsMatch("src/Program.cs", "src/Program.cs"));
        Assert.False(Wildcard.Glob.IsMatch("src/Program.cs", "src/Lib.cs"));
    }

    [Fact]
    public void IsMatch_InstanceMethod()
    {
        var glob = Wildcard.Glob.Parse("**/*.cs");
        Assert.True(glob.IsMatch("src/Program.cs"));
        Assert.False(glob.IsMatch("docs/readme.md"));
    }

    [Fact]
    public void IsMatch_NullPath_Throws()
    {
        var glob = Wildcard.Glob.Parse("**");
        Assert.Throws<ArgumentNullException>(() => glob.IsMatch(null!));
    }

    [Fact]
    public void IsMatch_ConsecutiveDoubleStars()
    {
        // **/**/*.cs should behave same as **/*.cs
        Assert.True(Wildcard.Glob.IsMatch("**/**/*.cs", "src/deep/File.cs"));
        Assert.False(Wildcard.Glob.IsMatch("**/**/*.cs", "readme.md"));
    }

    [Fact]
    public void SymlinkCycle_Terminates()
    {
        if (OperatingSystem.IsWindows()) return; // symlinks need elevation on Windows

        // Create a symlink cycle: tempdir/a/b/loop -> tempdir/a
        var dirA = Path.Combine(_tempDir, "a");
        var dirB = Path.Combine(_tempDir, "a", "b");
        Directory.CreateDirectory(dirB);
        File.WriteAllText(Path.Combine(dirA, "file.txt"), "hello");
        File.WriteAllText(Path.Combine(dirB, "file2.txt"), "world");
        Directory.CreateSymbolicLink(Path.Combine(dirB, "loop"), dirA);

        // Should terminate despite the cycle, not throw or hang
        var results = Wildcard.Glob.Match("**/*", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.Contains("a/file.txt", results);
        Assert.Contains("a/b/file2.txt", results);
    }
}

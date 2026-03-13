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

    // --- Brace expansion ---

    [Fact]
    public void BraceExpansion_MultipleExtensions()
    {
        var results = Glob("**/*.{cs,json}");
        Assert.Contains("src/Program.cs", results);
        Assert.Contains("src/Lib.cs", results);
        Assert.Contains("src/utils/Helper.cs", results);
        Assert.Contains("src/utils/data.json", results);
        Assert.Contains("src/deep/nested/File.cs", results);
        Assert.DoesNotContain("docs/readme.md", results);
        Assert.DoesNotContain("root.txt", results);
    }

    [Fact]
    public void BraceExpansion_MultipleDirectories()
    {
        var results = Glob("{src,docs}/*");
        Assert.Contains("src/Program.cs", results);
        Assert.Contains("src/Lib.cs", results);
        Assert.Contains("docs/readme.md", results);
    }

    [Fact]
    public void BraceExpansion_CrossSegment()
    {
        var results = Glob("{src/utils,docs}/*");
        Assert.Contains("src/utils/Helper.cs", results);
        Assert.Contains("src/utils/data.json", results);
        Assert.Contains("docs/readme.md", results);
    }

    [Fact]
    public void BraceExpansion_SingleAlternative_SameAsBare()
    {
        var withBraces = Glob("*.{txt}");
        var withoutBraces = Glob("*.txt");
        Assert.Equal(withoutBraces, withBraces);
    }

    [Fact]
    public void BraceExpansion_NoDuplicates()
    {
        var results = Glob("{src,src}/*.cs");
        // Each file should appear only once
        Assert.Equal(results.Distinct(StringComparer.OrdinalIgnoreCase).Count(), results.Count);
    }

    [Theory]
    [InlineData("**/*.{cs,md}", "src/Program.cs", true)]
    [InlineData("**/*.{cs,md}", "docs/readme.md", true)]
    [InlineData("**/*.{cs,md}", "root.txt", false)]
    [InlineData("**/*.{cs,md}", "src/utils/data.json", false)]
    [InlineData("{src,docs}/**/*", "src/Program.cs", true)]
    [InlineData("{src,docs}/**/*", "docs/readme.md", true)]
    [InlineData("{src,docs}/**/*", "root.txt", false)]
    [InlineData("*.{cs,md,json}", "file.cs", true)]
    [InlineData("*.{cs,md,json}", "file.md", true)]
    [InlineData("*.{cs,md,json}", "file.json", true)]
    [InlineData("*.{cs,md,json}", "file.txt", false)]
    public void IsMatch_BraceExpansion(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Wildcard.Glob.IsMatch(pattern, path));
    }

    // --- Symlink handling ---

    [Fact]
    public void Symlinks_NotFollowedByDefault()
    {
        if (OperatingSystem.IsWindows()) return; // symlinks need elevation on Windows

        // Create: tempdir/target/file.txt and tempdir/link -> tempdir/target
        var targetDir = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "file.txt"), "hello");
        Directory.CreateSymbolicLink(Path.Combine(_tempDir, "link"), targetDir);

        var results = Wildcard.Glob.Match("**/*", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        // Files in the real directory are found
        Assert.Contains("target/file.txt", results);
        // Symlinked directory is not traversed
        Assert.DoesNotContain("link/file.txt", results);
    }

    [Fact]
    public void Symlinks_FollowedWhenEnabled()
    {
        if (OperatingSystem.IsWindows()) return;

        var targetDir = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "file.txt"), "hello");
        Directory.CreateSymbolicLink(Path.Combine(_tempDir, "link"), targetDir);

        var options = new GlobOptions { FollowSymlinks = true };
        var results = Wildcard.Glob.Match("**/*", _tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.Contains("target/file.txt", results);
        Assert.Contains("link/file.txt", results);
    }

    [Fact]
    public void SymlinkCycle_SkippedByDefault()
    {
        if (OperatingSystem.IsWindows()) return;

        // Create a symlink cycle: tempdir/a/b/loop -> tempdir/a
        var dirA = Path.Combine(_tempDir, "a");
        var dirB = Path.Combine(_tempDir, "a", "b");
        Directory.CreateDirectory(dirB);
        File.WriteAllText(Path.Combine(dirA, "file.txt"), "hello");
        File.WriteAllText(Path.Combine(dirB, "file2.txt"), "world");
        Directory.CreateSymbolicLink(Path.Combine(dirB, "loop"), dirA);

        // Symlinks not followed by default — terminates without issue
        var results = Wildcard.Glob.Match("**/*", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.Contains("a/file.txt", results);
        Assert.Contains("a/b/file2.txt", results);
        // The symlink loop directory is not traversed
        Assert.DoesNotContain("a/b/loop/file.txt", results);
    }

    [Fact]
    public void SymlinkCycle_DetectedWhenFollowing()
    {
        if (OperatingSystem.IsWindows()) return;

        var dirA = Path.Combine(_tempDir, "a");
        var dirB = Path.Combine(_tempDir, "a", "b");
        Directory.CreateDirectory(dirB);
        File.WriteAllText(Path.Combine(dirA, "file.txt"), "hello");
        File.WriteAllText(Path.Combine(dirB, "file2.txt"), "world");
        Directory.CreateSymbolicLink(Path.Combine(dirB, "loop"), dirA);

        // With FollowSymlinks, cycle detection prevents infinite recursion
        var options = new GlobOptions { FollowSymlinks = true };
        var results = Wildcard.Glob.Match("**/*", _tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.Contains("a/file.txt", results);
        Assert.Contains("a/b/file2.txt", results);
        // Files through the symlink are found (one level before cycle detection)
        Assert.Contains("a/b/loop/file.txt", results);
        Assert.Contains("a/b/loop/b/file2.txt", results);
    }

    [Fact]
    public void SymlinkedFile_SkippedByDefault()
    {
        if (OperatingSystem.IsWindows()) return;

        var realFile = Path.Combine(_tempDir, "real.txt");
        File.WriteAllText(realFile, "content");
        File.CreateSymbolicLink(Path.Combine(_tempDir, "link.txt"), realFile);

        var results = Wildcard.Glob.Match("*.txt", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.Contains("real.txt", results);
        Assert.DoesNotContain("link.txt", results);
    }

    [Fact]
    public void SymlinkedFile_IncludedWhenFollowing()
    {
        if (OperatingSystem.IsWindows()) return;

        var realFile = Path.Combine(_tempDir, "real.txt");
        File.WriteAllText(realFile, "content");
        File.CreateSymbolicLink(Path.Combine(_tempDir, "link.txt"), realFile);

        var options = new GlobOptions { FollowSymlinks = true };
        var results = Wildcard.Glob.Match("*.txt", _tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.Contains("real.txt", results);
        Assert.Contains("link.txt", results);
    }

    // --- Merged single-traversal brace expansion ---

    [Fact]
    public void BraceExpansion_MergedTraversal_MatchesEquivalentUnion()
    {
        // **/*.{cs,json} should produce same results as union of **/*.cs and **/*.json
        var braceResults = Wildcard.Glob.Match("**/*.{cs,json}", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unionResults = Wildcard.Glob.Match("**/*.cs", _tempDir)
            .Concat(Wildcard.Glob.Match("**/*.json", _tempDir))
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(unionResults, braceResults);
    }

    [Fact]
    public void BraceExpansion_MergedTraversal_ThreeExtensions()
    {
        CreateFile("src/style.css");
        CreateFile("src/page.razor");

        var results = Wildcard.Glob.Match("**/*.{cs,css,razor}", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("src/Program.cs", results);
        Assert.Contains("src/style.css", results);
        Assert.Contains("src/page.razor", results);
        Assert.DoesNotContain("src/utils/data.json", results);
    }

    [Fact]
    public void BraceExpansion_MergedTraversal_NoDuplicates()
    {
        // *.{cs,cs} — both alternatives match the same files, should deduplicate
        var results = Wildcard.Glob.Match("**/*.{cs,cs}", _tempDir).ToList();
        var distinct = results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(distinct.Count, results.Count);
    }

    [Fact]
    public void BraceExpansion_NonMergeable_DifferentPrefixes_StillCorrect()
    {
        // Braces in directory segment can't merge — falls back to variants
        var results = Wildcard.Glob.Match("{src,docs}/*", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("src/Program.cs", results);
        Assert.Contains("src/Lib.cs", results);
        Assert.Contains("docs/readme.md", results);
    }

    [Fact]
    public void BraceExpansion_MergedTraversal_IsMatchConsistent()
    {
        // Verify IsMatch agrees with Match for the merged pattern
        var pattern = "**/*.{cs,json,md}";
        var matched = Wildcard.Glob.Match(pattern, _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        foreach (var path in matched)
            Assert.True(Wildcard.Glob.IsMatch(pattern, path), $"IsMatch should match '{path}'");

        // Non-matching extension
        Assert.False(Wildcard.Glob.IsMatch(pattern, "file.xyz"));
    }

    [Fact]
    public void BraceExpansion_MergedTraversal_SingleExtension_NoVariants()
    {
        // Single brace alternative — no expansion needed, should behave as plain glob
        var braceResults = Wildcard.Glob.Match("**/*.{cs}", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var plainResults = Wildcard.Glob.Match("**/*.cs", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(plainResults, braceResults);
    }

    [Fact]
    public void BraceExpansion_MergedTraversal_WithLiteralPrefix()
    {
        // src/*.{cs,json} — literal prefix + merged pattern segment
        var results = Wildcard.Glob.Match("src/*.{cs,json}", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("src/Lib.cs", results);
        Assert.Contains("src/Program.cs", results);
        Assert.DoesNotContain("src/utils/Helper.cs", results); // not direct child
        Assert.DoesNotContain("docs/readme.md", results);
    }

    [Fact]
    public void BraceExpansion_MergedTraversal_ParallelMatchesSequential()
    {
        // Verify parallel enumeration (Match) matches sequential (EnumerateMatches)
        var pattern = "**/*.{cs,md,json}";
        var sequential = Wildcard.Glob.Parse(pattern).EnumerateMatches(_tempDir)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var parallel = Wildcard.Glob.Match(pattern, _tempDir)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(sequential, parallel);
    }
}

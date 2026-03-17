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

    // --- System directory skipping ---

    [Theory]
    [InlineData("$RECYCLE.BIN")]
    [InlineData("System Volume Information")]
    [InlineData("$WinREAgent")]
    [InlineData(".Spotlight-V100")]
    [InlineData(".fseventsd")]
    [InlineData(".Trashes")]
    [InlineData(".TemporaryItems")]
    [InlineData(".DocumentRevisions-V100")]
    [InlineData("lost+found")]
    public void SystemDirectories_SkippedDuringTraversal(string systemDir)
    {
        // Create a file inside a system directory
        CreateFile($"{systemDir}/secret.txt");

        var results = Glob("**/*.txt");

        Assert.DoesNotContain(results, r => r.StartsWith(systemDir, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("root.txt", results);
    }

    [Theory]
    [InlineData("$RECYCLE.BIN")]
    [InlineData("System Volume Information")]
    [InlineData("$WinREAgent")]
    [InlineData(".Spotlight-V100")]
    [InlineData(".fseventsd")]
    [InlineData(".Trashes")]
    [InlineData(".TemporaryItems")]
    [InlineData(".DocumentRevisions-V100")]
    [InlineData("lost+found")]
    public void SystemDirectories_SkippedDuringTraversal_Parallel(string systemDir)
    {
        // Verify the parallel (channel-based) path also skips system directories
        CreateFile($"{systemDir}/deep/nested/file.cs");

        var results = Wildcard.Glob.Match("**/*.cs", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.DoesNotContain(results, r => r.StartsWith(systemDir, StringComparison.OrdinalIgnoreCase));
        // Normal files still found
        Assert.Contains(results, r => r == "src/Program.cs");
    }

    [Fact]
    public void SystemDirectories_SimilarNamesNotSkipped()
    {
        // Directories with similar but non-matching names should NOT be skipped
        CreateFile("Trashes/file.txt");
        CreateFile("spotlight/file.txt");
        CreateFile("recycle/file.txt");

        var results = Glob("**/*.txt");

        Assert.Contains("Trashes/file.txt", results);
        Assert.Contains("spotlight/file.txt", results);
        Assert.Contains("recycle/file.txt", results);
    }

    // --- Gitignore integration ---

    [Fact]
    public void Gitignore_RepoDetection_FiltersIgnoredFiles()
    {
        // Set up a fake git repo with .gitignore
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "*.log\nbuild/\n");

        CreateFile("app.cs");
        CreateFile("debug.log");
        CreateFile("build/output.dll");

        var options = new GlobOptions { RespectGitignore = true };
        var results = Wildcard.Glob.Match("**/*", _tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("app.cs", results);
        Assert.DoesNotContain("debug.log", results);
        Assert.DoesNotContain("build/output.dll", results);
    }

    [Fact]
    public void Gitignore_SkipsGitDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git", "objects"));
        File.WriteAllText(Path.Combine(_tempDir, ".git", "HEAD"), "ref: refs/heads/main");
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "");

        CreateFile("app.cs");

        var options = new GlobOptions { RespectGitignore = true };
        var results = Wildcard.Glob.Match("**/*", _tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.DoesNotContain(results, r => r.StartsWith(".git/", StringComparison.Ordinal));
        Assert.Contains("app.cs", results);
    }

    [Fact]
    public void Gitignore_NestedGitignoreRules()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "*.log\n");

        // Nested .gitignore in subdir
        var subDir = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, ".gitignore"), "*.tmp\n");

        CreateFile("sub/keep.cs");
        CreateFile("sub/remove.tmp");
        CreateFile("sub/also_remove.log");

        var options = new GlobOptions { RespectGitignore = true };
        var results = Wildcard.Glob.Match("**/*", _tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("sub/keep.cs", results);
        Assert.DoesNotContain("sub/remove.tmp", results);
        Assert.DoesNotContain("sub/also_remove.log", results);
    }

    [Fact]
    public void Gitignore_NegationRule_UnignoresFile()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "*.log\n!important.log\n");

        CreateFile("debug.log");
        CreateFile("important.log");

        var options = new GlobOptions { RespectGitignore = true };
        var results = Wildcard.Glob.Match("**/*", _tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.DoesNotContain("debug.log", results);
        Assert.Contains("important.log", results);
    }

    [Fact]
    public void Gitignore_DirectoryOnlyRule()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        // Trailing slash means only match directories
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "logs/\n");

        CreateFile("logs/app.log");
        CreateFile("logs.txt"); // file named "logs.txt" should NOT be ignored

        var options = new GlobOptions { RespectGitignore = true };
        var results = Wildcard.Glob.Match("**/*", _tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.DoesNotContain("logs/app.log", results);
        Assert.Contains("logs.txt", results);
    }

    [Fact]
    public void Gitignore_WithoutGitRepo_NoFiltering()
    {
        // No .git directory - gitignore should have no effect
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "*.cs\n");
        CreateFile("app.cs");

        var options = new GlobOptions { RespectGitignore = true };
        var results = Wildcard.Glob.Match("**/*", _tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        // .cs files should still be present since there's no .git directory
        Assert.Contains("app.cs", results);
    }

    [Fact]
    public void Gitignore_RespectGitignoreFalse_DoesNotFilter()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "*.log\n");

        CreateFile("debug.log");

        // Default options (RespectGitignore = false)
        var results = Wildcard.Glob.Match("**/*", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.Contains("debug.log", results);
    }

    [Fact]
    public void Gitignore_ParallelPath_FiltersIgnoredFiles()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "*.log\nobj/\n");

        CreateFile("app.cs");
        CreateFile("debug.log");
        CreateFile("obj/output.dll");
        CreateFile("obj/nested/deep.dll");

        var options = new GlobOptions { RespectGitignore = true };
        // Use the channel-based parallel path (Match calls WriteMatchesToChannel internally
        // when the pattern has ** segments that trigger parallel walk)
        var results = Wildcard.Glob.Match("**/*.{cs,dll,log}", _tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("app.cs", results);
        Assert.DoesNotContain("debug.log", results);
        Assert.DoesNotContain("obj/output.dll", results);
        Assert.DoesNotContain("obj/nested/deep.dll", results);
    }

    [Fact]
    public void Gitignore_SlashPrefixedPattern_MatchesRelativeToRoot()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        // Leading slash: only matches at root level
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "/build\n");

        CreateFile("build/output.dll");
        CreateFile("sub/build/output.dll"); // nested "build" should NOT be ignored

        var options = new GlobOptions { RespectGitignore = true };
        var results = Wildcard.Glob.Match("**/*", _tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.DoesNotContain("build/output.dll", results);
        Assert.Contains("sub/build/output.dll", results);
    }

    // --- Brace expansion with non-mergeable variants ---

    [Fact]
    public void BraceExpansion_NonMergeable_DifferentSegmentCounts()
    {
        // {src/*.cs,docs/sub/*.md} — different segment counts, cannot merge
        CreateFile("docs/sub/guide.md");
        var results = Wildcard.Glob.Match("{src/*.cs,docs/sub/*.md}", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("src/Lib.cs", results);
        Assert.Contains("src/Program.cs", results);
        Assert.Contains("docs/sub/guide.md", results);
    }

    [Fact]
    public void BraceExpansion_NonMergeable_DifferentPrefixLiterals()
    {
        // {src/**/*.cs,docs/**/*.md} — same segment count but different literal prefixes
        var results = Wildcard.Glob.Match("{src/**/*.cs,docs/**/*.md}", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("src/Program.cs", results);
        Assert.Contains("src/deep/nested/File.cs", results);
        Assert.Contains("docs/readme.md", results);
    }

    [Fact]
    public void BraceExpansion_NonMergeable_PatternInPrefix()
    {
        // {*rc/**/*.cs,docs/**/*.md} — pattern segment in prefix, merge bails out
        var results = Wildcard.Glob.Match("{*rc/**/*.cs,docs/**/*.md}", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("src/Program.cs", results);
        Assert.Contains("docs/readme.md", results);
    }

    [Fact]
    public void BraceExpansion_NonMergeable_LastSegmentNotPattern()
    {
        // {src/Program.cs,docs/readme.md} — last segment is literal, not pattern
        var results = Wildcard.Glob.Match("{src/Program.cs,docs/readme.md}", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("src/Program.cs", results);
        Assert.Contains("docs/readme.md", results);
    }

    [Fact]
    public void BraceExpansion_NonMergeable_NoDuplicatesAcrossVariants()
    {
        // Both variants can match the same file — dedup required
        var results = Wildcard.Glob.Match("{**/*.cs,src/**/*.cs}", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        var distinct = results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(distinct.Count, results.Count);
        Assert.Equal(4, results.Count); // all .cs files
    }

    // --- Multiple ** segments triggering deduplication ---

    [Fact]
    public void MultipleDoubleStars_Deduplicates_Sequential()
    {
        var results = Wildcard.Glob.Parse("**/**/*.cs").EnumerateMatches(_tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var expected = Wildcard.Glob.Parse("**/*.cs").EnumerateMatches(_tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(expected, results);
        // Verify no duplicates
        Assert.Equal(results.Distinct(StringComparer.OrdinalIgnoreCase).Count(), results.Count);
    }

    [Fact]
    public void MultipleDoubleStars_ThreeStars_Deduplicates()
    {
        var results = Wildcard.Glob.Match("**/**/**/*.cs", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Consecutive ** get collapsed in parsing, so this should behave like **/*.cs
        var expected = Wildcard.Glob.Match("**/*.cs", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(expected, results);
    }

    [Fact]
    public void MultipleDoubleStars_NonConsecutive_Deduplicates()
    {
        // src/**/**/File.cs — ** at two non-consecutive positions
        // Cannot be collapsed, triggers deduplication path
        CreateFile("src/a/b/File.cs");
        CreateFile("src/x/File.cs");

        var results = Wildcard.Glob.Parse("src/**/**/File.cs").EnumerateMatches(_tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Should find files but no duplicates
        var distinct = results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(distinct.Count, results.Count);
        Assert.Contains("src/deep/nested/File.cs", results);
    }

    // --- Absolute path patterns ---

    [Fact]
    public void AbsolutePath_WithDoubleStar_RespectsGitignore()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "*.log\n");

        CreateFile("app.cs");
        CreateFile("debug.log");

        var absPattern = Path.Combine(_tempDir, "**", "*").Replace('\\', '/');
        var options = new GlobOptions { RespectGitignore = true };
        var results = Wildcard.Glob.Match(absPattern, options: options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.Contains("app.cs", results);
        Assert.DoesNotContain("debug.log", results);
    }

    [Fact]
    public void AbsolutePath_WithBraceExpansion()
    {
        var absPattern = Path.Combine(_tempDir, "**", "*.{cs,md}").Replace('\\', '/');
        var results = Wildcard.Glob.Match(absPattern)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("src/Program.cs", results);
        Assert.Contains("docs/readme.md", results);
        Assert.DoesNotContain("root.txt", results);
    }

    [Fact]
    public void AbsolutePath_PatternInMiddleSegment()
    {
        var absPattern = Path.Combine(_tempDir, "s*", "*.cs").Replace('\\', '/');
        var results = Wildcard.Glob.Match(absPattern)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(["src/Lib.cs", "src/Program.cs"], results);
    }

    // --- Sequential EnumerateMatches path ---

    [Fact]
    public void EnumerateMatches_SequentialPath_LiteralPattern()
    {
        var results = Wildcard.Glob.Parse("src/Program.cs").EnumerateMatches(_tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.Equal(["src/Program.cs"], results);
    }

    [Fact]
    public void EnumerateMatches_SequentialPath_DoubleStarPattern()
    {
        var results = Wildcard.Glob.Parse("**/*.cs").EnumerateMatches(_tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal([
            "src/deep/nested/File.cs",
            "src/Lib.cs",
            "src/Program.cs",
            "src/utils/Helper.cs",
        ], results);
    }

    [Fact]
    public void EnumerateMatches_SequentialPath_WithVariants()
    {
        // Non-mergeable variants go through the _variants path in EnumerateMatches
        var results = Wildcard.Glob.Parse("{src,docs}/**/*").EnumerateMatches(_tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("src/Program.cs", results);
        Assert.Contains("docs/readme.md", results);
        Assert.DoesNotContain("root.txt", results);
        // Should have no duplicates
        Assert.Equal(results.Distinct(StringComparer.OrdinalIgnoreCase).Count(), results.Count);
    }

    [Fact]
    public void EnumerateMatches_SequentialPath_EmptySegments()
    {
        // A glob that parses to empty segments should return nothing
        var glob = Wildcard.Glob.Parse("{src,docs}/*");
        // This is the non-merged variant path — the outer Glob has empty _segments
        // but _variants is non-null
        var results = glob.EnumerateMatches(_tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.Contains("src/Program.cs", results);
    }

    [Fact]
    public void EnumerateMatches_SequentialPath_WithGitignore()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "*.json\n");

        var options = new GlobOptions { RespectGitignore = true };
        var results = Wildcard.Glob.Parse("src/**/*").EnumerateMatches(_tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("src/Program.cs", results);
        Assert.DoesNotContain("src/utils/data.json", results);
    }

    [Fact]
    public void EnumerateMatches_SequentialPath_StarInMiddle()
    {
        // Pattern segment in the middle that is not the last segment
        var results = Wildcard.Glob.Parse("src/*/Helper.cs").EnumerateMatches(_tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.Equal(["src/utils/Helper.cs"], results);
    }

    [Fact]
    public void EnumerateMatches_SequentialPath_DoubleStarOnly()
    {
        // Pattern is just ** — should match all files
        var results = Wildcard.Glob.Parse("**").EnumerateMatches(_tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.Equal(7, results.Count);
    }

    [Fact]
    public void EnumerateMatches_SequentialPath_DoubleStarTrailing()
    {
        // src/** — should match all files under src
        var results = Wildcard.Glob.Parse("src/**").EnumerateMatches(_tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.Equal(5, results.Count);
        Assert.Contains("src/Program.cs", results);
        Assert.Contains("src/deep/nested/File.cs", results);
    }

    // --- Parallel Match path via WriteMatchesToChannel ---

    [Fact]
    public void ParallelMatch_DoubleStarPattern_FindsAllFiles()
    {
        // Glob.Match uses the parallel WriteMatchesToChannel path internally
        var results = Wildcard.Glob.Match("**/*", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(7, results.Count);
    }

    [Fact]
    public void ParallelMatch_DeepTreeWithManyDirs()
    {
        // Create a wider tree to exercise parallel work-stealing
        for (int i = 0; i < 10; i++)
        {
            CreateFile($"wide/dir{i}/file.cs");
            CreateFile($"wide/dir{i}/sub/nested.cs");
        }

        var results = Wildcard.Glob.Match("wide/**/*.cs", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(20, results.Count);
    }

    [Fact]
    public void ParallelMatch_LiteralPattern_NoParallelNeeded()
    {
        var results = Wildcard.Glob.Match("src/Program.cs", _tempDir).ToList();

        Assert.Single(results);
        Assert.EndsWith("Program.cs", results[0]);
    }

    [Fact]
    public void ParallelMatch_PatternSegment_InMiddle()
    {
        // Pattern segment followed by more segments
        var results = Wildcard.Glob.Match("src/*/data.json", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.Equal(["src/utils/data.json"], results);
    }

    [Fact]
    public void ParallelMatch_WithCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        // Cancelled token should throw or return empty
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Wildcard.Glob.Parse("**/*").WriteMatchesToChannel(channel.Writer, _tempDir, cancellationToken: cts.Token));
    }

    [Fact]
    public void ParallelMatch_NonMergeableVariants_WritesToChannel()
    {
        // Non-mergeable variants with ** go through the DeduplicatingChannelWriter path
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        Wildcard.Glob.Parse("{src,docs}/**/*").WriteMatchesToChannel(channel.Writer, _tempDir);
        channel.Writer.Complete();

        var results = new List<string>();
        while (channel.Reader.TryRead(out var item))
            results.Add(Path.GetRelativePath(_tempDir, item).Replace('\\', '/'));

        Assert.Contains("src/Program.cs", results);
        Assert.Contains("docs/readme.md", results);
        // No duplicates
        Assert.Equal(results.Distinct(StringComparer.OrdinalIgnoreCase).Count(), results.Count);
    }

    [Fact]
    public void ParallelMatch_MergedVariants_WritesToChannel()
    {
        // Merged variants (braces in filename) via channel
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        Wildcard.Glob.Parse("**/*.{cs,json}").WriteMatchesToChannel(channel.Writer, _tempDir);
        channel.Writer.Complete();

        var results = new List<string>();
        while (channel.Reader.TryRead(out var item))
            results.Add(Path.GetRelativePath(_tempDir, item).Replace('\\', '/'));

        Assert.Contains("src/Program.cs", results);
        Assert.Contains("src/utils/data.json", results);
        Assert.DoesNotContain("docs/readme.md", results);
    }

    [Fact]
    public void MatchToChannel_StaticConvenience()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        Wildcard.Glob.MatchToChannel("**/*.cs", channel.Writer, _tempDir);
        channel.Writer.Complete();

        var results = new List<string>();
        while (channel.Reader.TryRead(out var item))
            results.Add(item);

        Assert.Equal(4, results.Count);
    }

    // --- EnumerateAllSubdirectoriesFiltered fast path ---

    [Fact]
    public void FastPath_NoGitignore_NoSymlinks_EnumeratesAll()
    {
        // Default options (no gitignore, no symlinks) should use the fast
        // EnumerateAllDirectoriesSafe path inside EnumerateAllSubdirectoriesFiltered
        CreateFile("deep1/deep2/deep3/file.cs");

        var results = Wildcard.Glob.Parse("**/*.cs").EnumerateMatches(_tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("deep1/deep2/deep3/file.cs", results);
        Assert.Contains("src/deep/nested/File.cs", results);
    }

    [Fact]
    public void FastPath_WithGitignore_UsesFilteredPath()
    {
        // With gitignore, should use the filtered (manual recursive) path
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "ignored_dir/\n");

        CreateFile("ignored_dir/deep/file.cs");
        CreateFile("kept_dir/deep/file.cs");

        var options = new GlobOptions { RespectGitignore = true };
        var results = Wildcard.Glob.Parse("**/*.cs").EnumerateMatches(_tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.DoesNotContain("ignored_dir/deep/file.cs", results);
        Assert.Contains("kept_dir/deep/file.cs", results);
    }

    [Fact]
    public void FastPath_SystemDirsSkipped_InEnumerateAllDirectoriesSafe()
    {
        // The fast path uses EnumerateAllDirectoriesSafe which has its own system dir filtering
        CreateFile(".Trashes/nested/deep/file.cs");
        CreateFile("normal/nested/deep/file.cs");

        var results = Wildcard.Glob.Parse("**/*.cs").EnumerateMatches(_tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.DoesNotContain(".Trashes/nested/deep/file.cs", results);
        Assert.Contains("normal/nested/deep/file.cs", results);
    }

    // --- Edge cases for empty results and traversal ---

    [Fact]
    public void EmptySegments_ReturnsNothing()
    {
        // Pattern that expands to variants with empty segments
        // The outer Glob has _segments = [] when _variants is set
        // Verify that patterns resolving to nonexistent paths return empty
        var results = Wildcard.Glob.Match("nonexistent_dir/**/*.cs", _tempDir).ToList();
        Assert.Empty(results);
    }

    [Fact]
    public void LiteralDir_NotExists_ReturnsEmpty()
    {
        var results = Wildcard.Glob.Match("missing/sub/file.cs", _tempDir).ToList();
        Assert.Empty(results);
    }

    [Fact]
    public void PatternSegment_NoMatchingDirs_ReturnsEmpty()
    {
        var results = Wildcard.Glob.Match("zzz*/file.cs", _tempDir).ToList();
        Assert.Empty(results);
    }

    [Fact]
    public void DoubleStar_TerminalSegment_MatchesFilesInDir()
    {
        // When ** is the last segment and consumes into a directory,
        // it should enumerate files in that directory
        var results = Wildcard.Glob.Parse("src/utils/**").EnumerateMatches(_tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("src/utils/data.json", results);
        Assert.Contains("src/utils/Helper.cs", results);
    }

    [Fact]
    public void IsMatch_NonMergeableVariants()
    {
        // IsMatch with variants that cannot be merged
        var glob = Wildcard.Glob.Parse("{src,docs}/**/*.cs");
        Assert.True(glob.IsMatch("src/Program.cs"));
        Assert.True(glob.IsMatch("src/deep/File.cs"));
        Assert.False(glob.IsMatch("other/File.cs"));
        Assert.False(glob.IsMatch("docs/readme.md"));
    }

    [Fact]
    public void WriteBlocking_DirectWrite()
    {
        // Test WriteBlocking static method directly
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        Wildcard.Glob.WriteBlocking(channel.Writer, "test-value");
        Assert.True(channel.Reader.TryRead(out var value));
        Assert.Equal("test-value", value);
    }

    [Fact]
    public async Task WriteBlocking_BoundedChannel()
    {
        // Test WriteBlocking with a bounded channel (exercises slow path when full)
        var channel = System.Threading.Channels.Channel.CreateBounded<string>(1);
        Wildcard.Glob.WriteBlocking(channel.Writer, "first");

        // Channel is full, write in background should eventually succeed
        var writeTask = Task.Run(() => Wildcard.Glob.WriteBlocking(channel.Writer, "second"));

        // Read the first item to make room
        Assert.True(channel.Reader.TryRead(out var first));
        Assert.Equal("first", first);

        // The background write should complete
        await writeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(channel.Reader.TryRead(out var second));
        Assert.Equal("second", second);
    }

    [Fact]
    public void EnumerateMatches_NullBaseDirectory_UsesCurrentDir()
    {
        // When baseDirectory is null, should use current working directory
        // Just verify it doesn't throw
        var results = Wildcard.Glob.Parse("*.nonexistent_extension_xyz").EnumerateMatches(null).ToList();
        Assert.Empty(results);
    }

    [Fact]
    public void Gitignore_WildcardPatternInGitignore()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "*.min.*\ntemp_*\n");

        CreateFile("app.min.js");
        CreateFile("app.js");
        CreateFile("temp_data.txt");
        CreateFile("data.txt");

        var options = new GlobOptions { RespectGitignore = true };
        var results = Wildcard.Glob.Match("**/*", _tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.DoesNotContain("app.min.js", results);
        Assert.Contains("app.js", results);
        Assert.DoesNotContain("temp_data.txt", results);
        Assert.Contains("data.txt", results);
    }

    [Fact]
    public void Gitignore_CommentAndBlankLinesIgnored()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "# This is a comment\n\n*.log\n\n# Another comment\n");

        CreateFile("debug.log");
        CreateFile("app.cs");

        var options = new GlobOptions { RespectGitignore = true };
        var results = Wildcard.Glob.Match("**/*", _tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .ToList();

        Assert.DoesNotContain("debug.log", results);
        Assert.Contains("app.cs", results);
    }

    [Fact]
    public void ParallelMatch_MultipleDoubleStars_Deduplicates()
    {
        // Multiple ** in pattern triggers DeduplicatingChannelWriter in WriteMatchesToChannel
        CreateFile("a/b/c/file.cs");

        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        // src/**/**/File.cs has two non-consecutive ** segments
        Wildcard.Glob.Parse("**/**/*.cs").WriteMatchesToChannel(channel.Writer, _tempDir);
        channel.Writer.Complete();

        var results = new List<string>();
        while (channel.Reader.TryRead(out var item))
            results.Add(item);

        var distinct = results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(distinct.Count, results.Count);
    }

    [Fact]
    public void Gitignore_DiscoveredDuringDoubleStar_Sequential()
    {
        // Git repo discovered mid-traversal during ** expansion
        var subRepo = Path.Combine(_tempDir, "sub_repo");
        Directory.CreateDirectory(Path.Combine(subRepo, ".git"));
        File.WriteAllText(Path.Combine(subRepo, ".gitignore"), "*.tmp\n");

        CreateFile("sub_repo/keep.cs");
        CreateFile("sub_repo/remove.tmp");
        CreateFile("sub_repo/inner/also_keep.cs");
        CreateFile("sub_repo/inner/also_remove.tmp");

        var options = new GlobOptions { RespectGitignore = true };
        var results = Wildcard.Glob.Parse("**/*").EnumerateMatches(_tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("sub_repo/keep.cs", results);
        Assert.Contains("sub_repo/inner/also_keep.cs", results);
        Assert.DoesNotContain("sub_repo/remove.tmp", results);
        Assert.DoesNotContain("sub_repo/inner/also_remove.tmp", results);
    }

    // --- Channel path coverage tests (WriteMatchesToChannel / WriteMatchesSegmentsCore) ---

    private List<string> GlobViaChannel(string pattern, GlobOptions? options = null)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        Wildcard.Glob.MatchToChannel(pattern, channel.Writer, _tempDir, options);
        channel.Writer.Complete();
        var results = new List<string>();
        while (channel.Reader.TryRead(out var item))
            results.Add(Path.GetRelativePath(_tempDir, item).Replace('\\', '/'));
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    [Fact]
    public void ChannelPath_LiteralAndPatternSegments()
    {
        // Exercises Literal segment (src) + Pattern segment as last (*.cs) in WriteMatchesSegmentsCore
        // Covers lines 540-553 (Literal) and 557-566 (Pattern, isLast)
        var results = GlobViaChannel("src/*.cs");
        Assert.Contains("src/Lib.cs", results);
        Assert.Contains("src/Program.cs", results);
        Assert.DoesNotContain("src/utils/Helper.cs", results);
        Assert.DoesNotContain("src/utils/data.json", results);
    }

    [Fact]
    public void ChannelPath_PatternSegmentNotLast()
    {
        // Exercises Pattern segment (not last) in WriteMatchesSegmentsCore (lines 568-585)
        var results = GlobViaChannel("s*/utils/*.cs");
        Assert.Equal(["src/utils/Helper.cs"], results);
    }

    [Fact]
    public void ChannelPath_PatternSegmentNotLast_MultipleMatchingDirs()
    {
        CreateFile("stuff/utils/Extra.cs");
        CreateFile("stuff/utils/notes.txt");

        var results = GlobViaChannel("s*/utils/*.cs");
        Assert.Contains("src/utils/Helper.cs", results);
        Assert.Contains("stuff/utils/Extra.cs", results);
        Assert.DoesNotContain("stuff/utils/notes.txt", results);
    }

    [Fact]
    public void ChannelPath_WithGitignore()
    {
        // Exercises WriteMatchesToChannel with gitignore enabled (lines 439-448)
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "*.json\n");

        CreateFile("src/extra.cs");
        CreateFile("src/extra.json");

        var options = new GlobOptions { RespectGitignore = true };
        var results = GlobViaChannel("**/*.json", options);

        // .gitignore excludes *.json, so no results even though files exist
        Assert.Empty(results);
    }

    [Fact]
    public void ChannelPath_WithGitignore_KeepsNonIgnored()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "*.json\n");

        var options = new GlobOptions { RespectGitignore = true };
        var results = GlobViaChannel("**/*.cs", options);

        Assert.Contains("src/Lib.cs", results);
        Assert.Contains("src/Program.cs", results);
        Assert.Contains("src/utils/Helper.cs", results);
        Assert.Contains("src/deep/nested/File.cs", results);
    }

    [Fact]
    public void ChannelPath_DoubleStarWithLiteral()
    {
        // Exercises DoubleStar in WriteMatchesSegmentsCore (lines 589+)
        // followed by Literal segments
        var results = GlobViaChannel("**/utils/Helper.cs");
        Assert.Equal(["src/utils/Helper.cs"], results);
    }

    [Fact]
    public void ChannelPath_DoubleStarWithLiteral_DeepNesting()
    {
        CreateFile("a/b/utils/Deep.cs");
        var results = GlobViaChannel("**/utils/Deep.cs");
        Assert.Equal(["a/b/utils/Deep.cs"], results);
    }

    [Fact]
    public void ChannelPath_NestedGitignoreInDoubleStarWalk()
    {
        // Exercises nested .gitignore loading during DoubleStar parallel walk
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "*.log\n");

        // Nested .gitignore in a subdirectory adds more exclusions
        File.WriteAllText(Path.Combine(_tempDir, "src", ".gitignore"), "*.tmp\n");

        CreateFile("root.log");
        CreateFile("src/build.log");
        CreateFile("src/cache.tmp");
        CreateFile("src/code.cs");
        CreateFile("docs/notes.log");
        CreateFile("docs/guide.tmp");

        var options = new GlobOptions { RespectGitignore = true };
        var results = GlobViaChannel("**/*", options);

        // *.log is ignored everywhere (root .gitignore)
        Assert.DoesNotContain("root.log", results);
        Assert.DoesNotContain("src/build.log", results);
        Assert.DoesNotContain("docs/notes.log", results);

        // *.tmp is ignored only under src/ (nested .gitignore)
        Assert.DoesNotContain("src/cache.tmp", results);
        Assert.Contains("docs/guide.tmp", results);

        // Normal files are kept
        Assert.Contains("src/code.cs", results);
    }

    [Fact]
    public void ChannelPath_MultipleDoubleStarSegments_Deduplication()
    {
        // Exercises WriteMatchesToChannel deduplication via ConcurrentDictionary (lines 457-461)
        CreateFile("a/b/c/file.cs");
        CreateFile("a/b/other.cs");

        var results = GlobViaChannel("**/b/**/*.cs");
        Assert.Contains("a/b/c/file.cs", results);
        Assert.Contains("a/b/other.cs", results);
        // Each file should appear exactly once despite multiple ** traversal paths
        Assert.Equal(results.Distinct(StringComparer.OrdinalIgnoreCase).Count(), results.Count);
    }

    [Fact]
    public void ChannelPath_GitignoreWithDirectoryExclusion()
    {
        // Exercises gitignore directory filtering in the channel DoubleStar walk
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "build/\nobj/\n");

        CreateFile("build/output.dll");
        CreateFile("build/output.pdb");
        CreateFile("obj/debug/temp.cs");
        CreateFile("src/main.cs");

        var options = new GlobOptions { RespectGitignore = true };
        var results = GlobViaChannel("**/*.cs", options);

        Assert.DoesNotContain("obj/debug/temp.cs", results);
        Assert.Contains("src/main.cs", results);
    }

    [Fact]
    public void ChannelPath_DoubleStarOnly_EnumeratesAllFiles()
    {
        // Pattern ** with only one DoubleStar segment: when zero-level expansion fires,
        // segmentIndex >= _segments.Length with currentDir being a directory,
        // exercising lines 520-532 (enumerate all files in the directory)
        var results = GlobViaChannel("**");
        Assert.Contains("root.txt", results);
        Assert.Contains("src/Program.cs", results);
        Assert.Contains("src/utils/Helper.cs", results);
        Assert.Contains("docs/readme.md", results);
    }

    [Fact]
    public void ChannelPath_LiteralPattern_DirectFile()
    {
        // Literal-only pattern in channel path — no ** or wildcards
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        Wildcard.Glob.Parse("src/Program.cs").WriteMatchesToChannel(channel.Writer, _tempDir);
        channel.Writer.Complete();

        var results = new List<string>();
        while (channel.Reader.TryRead(out var item))
            results.Add(Path.GetRelativePath(_tempDir, item).Replace('\\', '/'));

        Assert.Equal(["src/Program.cs"], results);
    }

    [Fact]
    public void ChannelPath_DoubleStarThenLiteral_MatchesFile()
    {
        // DoubleStar + literal file via channel path
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        Wildcard.Glob.Parse("**/Helper.cs").WriteMatchesToChannel(channel.Writer, _tempDir);
        channel.Writer.Complete();

        var results = new List<string>();
        while (channel.Reader.TryRead(out var item))
            results.Add(Path.GetRelativePath(_tempDir, item).Replace('\\', '/'));

        Assert.Contains("src/utils/Helper.cs", results);
    }

    [Fact]
    public void ChannelPath_PatternSegment_LastSegment()
    {
        // Pattern segment as last segment in channel path
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        Wildcard.Glob.Parse("src/[PL]*.cs").WriteMatchesToChannel(channel.Writer, _tempDir);
        channel.Writer.Complete();

        var results = new List<string>();
        while (channel.Reader.TryRead(out var item))
            results.Add(Path.GetRelativePath(_tempDir, item).Replace('\\', '/'));

        Assert.Contains("src/Program.cs", results);
        Assert.Contains("src/Lib.cs", results);
    }

    // --- Sequential path (EnumerateMatches / MatchSegmentsCore) coverage ---

    [Fact]
    public void Sequential_MultipleDoubleStarSegments_Deduplication()
    {
        // Exercises the HashSet deduplication path in EnumerateMatches (lines 396-401)
        CreateFile("a/b/c/file.cs");
        CreateFile("a/b/other.cs");

        var results = Glob("**/b/**/*.cs");
        Assert.Contains("a/b/c/file.cs", results);
        Assert.Contains("a/b/other.cs", results);
        Assert.Equal(results.Distinct(StringComparer.OrdinalIgnoreCase).Count(), results.Count);
    }

    [Fact]
    public void Sequential_MatchSegmentsCore_GitignoreFilteredSubdirs()
    {
        // Exercises line 770 (MatchSegmentsCore with gitignore and EnumerateAllSubdirectoriesFiltered)
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "node_modules/\n");

        CreateFile("node_modules/pkg/index.js");
        CreateFile("src/app.cs");

        var options = new GlobOptions { RespectGitignore = true };
        var results = Wildcard.Glob.Match("**/*.cs", _tempDir, options)
            .Select(p => Path.GetRelativePath(_tempDir, p).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("src/app.cs", results);
        Assert.DoesNotContain("node_modules/pkg/index.js", results);
    }
}

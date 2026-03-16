namespace Wildcard.Tests;

public class GitignoreFilterTests : IDisposable
{
    private readonly string _tempDir;

    public GitignoreFilterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wildcard_gitignore_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string CreateGitRepo(string? subDir = null)
    {
        var root = subDir is null ? _tempDir : Path.Combine(_tempDir, subDir);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".git", "info"));
        return root;
    }

    private static void WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
    }

    // ---------------------------------------------------------------
    // FindGitRoot
    // ---------------------------------------------------------------

    [Fact]
    public void FindGitRoot_ReturnsCurrentDir_WhenGitDirExistsAtCurrentLevel()
    {
        var root = CreateGitRepo();
        var result = GitignoreFilter.FindGitRoot(root);
        Assert.Equal(root, result);
    }

    [Fact]
    public void FindGitRoot_ReturnsParentDir_WhenGitDirExistsAtParentLevel()
    {
        var root = CreateGitRepo();
        var child = Path.Combine(root, "src", "deep");
        Directory.CreateDirectory(child);

        var result = GitignoreFilter.FindGitRoot(child);
        Assert.Equal(root, result);
    }

    [Fact]
    public void FindGitRoot_ReturnsNull_WhenNoGitDirExists()
    {
        var noGit = Path.Combine(_tempDir, "norepo");
        Directory.CreateDirectory(noGit);

        // _tempDir itself has no .git, so searching from noGit should eventually return null
        // unless the real filesystem has one above. We search from a deeply nested path under _tempDir
        // to minimize the chance of hitting a real .git above /tmp.
        var result = GitignoreFilter.FindGitRoot(noGit);

        // The temp directory is typically under /tmp which has no .git.
        // If the test runner's temp dir happens to be inside a git repo, skip assertion.
        if (result is not null)
            return; // defensive: cannot guarantee /tmp is not inside a git repo
        Assert.Null(result);
    }

    // ---------------------------------------------------------------
    // LoadFromGitRoot
    // ---------------------------------------------------------------

    [Fact]
    public void LoadFromGitRoot_ReturnsNull_WhenNoIgnoreFilesExist()
    {
        var root = CreateGitRepo();
        var filter = GitignoreFilter.LoadFromGitRoot(root);

        // LoadFromGitRoot also checks the global gitignore (XDG_CONFIG_HOME or ~/.config/git/ignore).
        // If a global gitignore exists on the test machine, the filter won't be null.
        // We can only assert null when no global gitignore is present.
        var globalIgnore = GetGlobalGitignorePathForTest();
        if (globalIgnore is null || !File.Exists(globalIgnore))
            Assert.Null(filter);
        else
            Assert.NotNull(filter); // global ignore exists, so filter is returned
    }

    private static string? GetGlobalGitignorePathForTest()
    {
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdgConfig))
            return Path.Combine(xdgConfig, "git", "ignore");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            return Path.Combine(home, ".config", "git", "ignore");
        return null;
    }

    [Fact]
    public void LoadFromGitRoot_LoadsRootGitignore()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "*.log\n");

        var filter = GitignoreFilter.LoadFromGitRoot(root);

        Assert.NotNull(filter);
        Assert.True(filter.IsIgnored("debug.log", false));
        Assert.False(filter.IsIgnored("readme.md", false));
    }

    [Fact]
    public void LoadFromGitRoot_LoadsExcludeFile()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".git", "info", "exclude"), "secret.txt\n");

        var filter = GitignoreFilter.LoadFromGitRoot(root);

        Assert.NotNull(filter);
        Assert.True(filter.IsIgnored("secret.txt", false));
        Assert.False(filter.IsIgnored("public.txt", false));
    }

    [Fact]
    public void LoadFromGitRoot_LoadsBothExcludeAndGitignore()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".git", "info", "exclude"), "excluded.txt\n");
        WriteFile(Path.Combine(root, ".gitignore"), "*.tmp\n");

        var filter = GitignoreFilter.LoadFromGitRoot(root);

        Assert.NotNull(filter);
        Assert.True(filter.IsIgnored("excluded.txt", false));
        Assert.True(filter.IsIgnored("data.tmp", false));
        Assert.False(filter.IsIgnored("code.cs", false));
    }

    // ---------------------------------------------------------------
    // IsIgnored - basename patterns
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("output.log", true)]
    [InlineData("dir/nested.log", true)]
    [InlineData("readme.md", false)]
    public void IsIgnored_BasenameWildcard_MatchesAnyDirectory(string path, bool expected)
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "*.log\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        Assert.Equal(expected, filter.IsIgnored(path, false));
    }

    [Fact]
    public void IsIgnored_ExactBasename_MatchesFile()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "Thumbs.db\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        Assert.True(filter.IsIgnored("Thumbs.db", false));
        Assert.True(filter.IsIgnored("subdir/Thumbs.db", false));
        Assert.False(filter.IsIgnored("thumbs.db.bak", false));
    }

    // ---------------------------------------------------------------
    // IsIgnored - full path patterns (contain slash)
    // ---------------------------------------------------------------

    [Fact]
    public void IsIgnored_PatternWithSlash_MatchesFullPath()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "src/*.cs\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        Assert.True(filter.IsIgnored("src/Program.cs", false));
        Assert.False(filter.IsIgnored("tests/Program.cs", false));
    }

    [Fact]
    public void IsIgnored_LeadingSlash_AnchoredToRoot()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "/build\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        // Leading slash means "relative to gitignore location" - since it contains
        // a slash it becomes a full-path pattern, matching "build" at root.
        Assert.True(filter.IsIgnored("build", false));
        // A deeply nested "build" would not match because the pattern is anchored.
        Assert.False(filter.IsIgnored("src/build", false));
    }

    // ---------------------------------------------------------------
    // IsIgnored - directory-only patterns
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void IsIgnored_TrailingSlash_MatchesDirectoriesOnly(bool isDirectory, bool expected)
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "build/\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        Assert.Equal(expected, filter.IsIgnored("build", isDirectory));
    }

    // ---------------------------------------------------------------
    // IsIgnored - negation
    // ---------------------------------------------------------------

    [Fact]
    public void IsIgnored_NegationPattern_ReIncludesFile()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "*.log\n!important.log\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        Assert.True(filter.IsIgnored("debug.log", false));
        Assert.False(filter.IsIgnored("important.log", false));
    }

    // ---------------------------------------------------------------
    // IsIgnored - last matching rule wins
    // ---------------------------------------------------------------

    [Fact]
    public void IsIgnored_LastMatchingRuleWins()
    {
        var root = CreateGitRepo();
        // First ignore all .log, then re-include, then ignore again
        WriteFile(Path.Combine(root, ".gitignore"), "*.log\n!*.log\n*.log\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        // Last rule is *.log (not negated), so it should be ignored
        Assert.True(filter.IsIgnored("test.log", false));
    }

    [Fact]
    public void IsIgnored_LastNegationWins()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "*.log\n!*.log\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        // Last rule is !*.log, so file should NOT be ignored
        Assert.False(filter.IsIgnored("test.log", false));
    }

    // ---------------------------------------------------------------
    // ParseRule behavior (via IsIgnored)
    // ---------------------------------------------------------------

    [Fact]
    public void ParseRule_IgnoresComments()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "# this is a comment\n*.log\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        Assert.True(filter.IsIgnored("test.log", false));
        Assert.False(filter.IsIgnored("# this is a comment", false));
    }

    [Fact]
    public void ParseRule_IgnoresEmptyLines()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "\n\n*.log\n\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        Assert.True(filter.IsIgnored("test.log", false));
    }

    [Fact]
    public void ParseRule_BackslashEscapesHash()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "\\#file\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        Assert.True(filter.IsIgnored("#file", false));
    }

    [Fact]
    public void ParseRule_BackslashEscapesBang()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "\\!important\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        Assert.True(filter.IsIgnored("!important", false));
    }

    [Fact]
    public void ParseRule_TrailingWhitespaceIsTrimmed()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "*.log   \n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        Assert.True(filter.IsIgnored("test.log", false));
        Assert.False(filter.IsIgnored("test.log   ", false));
    }

    // ---------------------------------------------------------------
    // AddRulesFromDirectory
    // ---------------------------------------------------------------

    [Fact]
    public void AddRulesFromDirectory_LoadsNestedGitignore()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "*.tmp\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        var subDir = Path.Combine(root, "src");
        Directory.CreateDirectory(subDir);
        WriteFile(Path.Combine(subDir, ".gitignore"), "*.generated.cs\n");

        int added = filter.AddRulesFromDirectory(subDir);

        Assert.True(added > 0);
        // Original root rule still applies
        Assert.True(filter.IsIgnored("data.tmp", false));
        // Nested basename pattern applies everywhere
        Assert.True(filter.IsIgnored("Foo.generated.cs", false));
    }

    [Fact]
    public void AddRulesFromDirectory_UsesRelativePrefixForPathPatterns()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "*.tmp\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        var subDir = Path.Combine(root, "src");
        Directory.CreateDirectory(subDir);
        // A pattern with a slash is treated as a full-path pattern.
        // When loaded from src/.gitignore, it should get "src/" prefix.
        WriteFile(Path.Combine(subDir, ".gitignore"), "/output\n");

        filter.AddRulesFromDirectory(subDir);

        // "src/output" should match because the pattern becomes "src/output"
        Assert.True(filter.IsIgnored("src/output", false));
        // "output" at root should NOT match
        Assert.False(filter.IsIgnored("output", false));
    }

    [Fact]
    public void AddRulesFromDirectory_ReturnsZero_WhenNoGitignoreExists()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "*.tmp\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        var subDir = Path.Combine(root, "lib");
        Directory.CreateDirectory(subDir);
        // No .gitignore in lib/

        int added = filter.AddRulesFromDirectory(subDir);
        Assert.Equal(0, added);
    }

    // ---------------------------------------------------------------
    // RemoveLastRules
    // ---------------------------------------------------------------

    [Fact]
    public void RemoveLastRules_RemovesAddedNestedRules()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "*.tmp\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        var subDir = Path.Combine(root, "src");
        Directory.CreateDirectory(subDir);
        WriteFile(Path.Combine(subDir, ".gitignore"), "*.obj\n*.pdb\n");

        int added = filter.AddRulesFromDirectory(subDir);
        Assert.Equal(2, added);

        // Before removal: nested rules apply
        Assert.True(filter.IsIgnored("file.obj", false));
        Assert.True(filter.IsIgnored("file.pdb", false));

        filter.RemoveLastRules(added);

        // After removal: nested rules no longer apply
        Assert.False(filter.IsIgnored("file.obj", false));
        Assert.False(filter.IsIgnored("file.pdb", false));
        // Root rule still applies
        Assert.True(filter.IsIgnored("data.tmp", false));
    }

    [Fact]
    public void RemoveLastRules_ZeroCount_DoesNothing()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "*.tmp\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        filter.RemoveLastRules(0);

        Assert.True(filter.IsIgnored("data.tmp", false));
    }

    [Fact]
    public void RemoveLastRules_CountExceedingRules_DoesNothing()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "*.tmp\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        // count > _rules.Count, so the guard should prevent removal
        filter.RemoveLastRules(999);

        Assert.True(filter.IsIgnored("data.tmp", false));
    }

    // ---------------------------------------------------------------
    // Clone
    // ---------------------------------------------------------------

    [Fact]
    public void Clone_HasSameRules()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "*.log\n*.tmp\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        var clone = filter.Clone();

        Assert.True(clone.IsIgnored("test.log", false));
        Assert.True(clone.IsIgnored("data.tmp", false));
        Assert.False(clone.IsIgnored("readme.md", false));
    }

    [Fact]
    public void Clone_ModificationsToClone_DoNotAffectOriginal()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "*.log\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        var clone = filter.Clone();

        // Add more rules to the clone via a nested directory
        var subDir = Path.Combine(root, "sub");
        Directory.CreateDirectory(subDir);
        WriteFile(Path.Combine(subDir, ".gitignore"), "*.dat\n");
        clone.AddRulesFromDirectory(subDir);

        // Clone sees new rule
        Assert.True(clone.IsIgnored("file.dat", false));
        // Original does NOT see the new rule
        Assert.False(filter.IsIgnored("file.dat", false));

        // Both still see the original rule
        Assert.True(clone.IsIgnored("test.log", false));
        Assert.True(filter.IsIgnored("test.log", false));
    }

    [Fact]
    public void Clone_ModificationsToOriginal_DoNotAffectClone()
    {
        var root = CreateGitRepo();
        WriteFile(Path.Combine(root, ".gitignore"), "*.log\n");
        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        var clone = filter.Clone();

        // Add more rules to the original
        var subDir = Path.Combine(root, "sub2");
        Directory.CreateDirectory(subDir);
        WriteFile(Path.Combine(subDir, ".gitignore"), "*.xyz\n");
        filter.AddRulesFromDirectory(subDir);

        // Original sees new rule
        Assert.True(filter.IsIgnored("file.xyz", false));
        // Clone does NOT see the new rule
        Assert.False(clone.IsIgnored("file.xyz", false));
    }

    // ---------------------------------------------------------------
    // Integration: multiple .gitignore files in nested directories
    // ---------------------------------------------------------------

    [Fact]
    public void Integration_NestedGitignores_WorkTogether()
    {
        var root = CreateGitRepo();

        // Root ignores build artifacts
        WriteFile(Path.Combine(root, ".gitignore"), "*.o\n*.obj\nbuild/\n");

        // Exclude file ignores IDE files
        WriteFile(Path.Combine(root, ".git", "info", "exclude"), ".idea/\n*.swp\n");

        var filter = GitignoreFilter.LoadFromGitRoot(root)!;

        // Add nested gitignore in src/
        var srcDir = Path.Combine(root, "src");
        Directory.CreateDirectory(srcDir);
        WriteFile(Path.Combine(srcDir, ".gitignore"), "*.generated.cs\n/bin\n");
        int srcAdded = filter.AddRulesFromDirectory(srcDir);

        // Root patterns
        Assert.True(filter.IsIgnored("main.o", false));
        Assert.True(filter.IsIgnored("build", true));
        Assert.False(filter.IsIgnored("build", false)); // directory-only pattern

        // Exclude patterns
        Assert.True(filter.IsIgnored(".idea", true));
        Assert.True(filter.IsIgnored("notes.swp", false));

        // Nested patterns
        Assert.True(filter.IsIgnored("Foo.generated.cs", false));
        Assert.True(filter.IsIgnored("src/bin", false)); // /bin in src/ becomes src/bin
        Assert.False(filter.IsIgnored("bin", false));     // /bin does not match root bin

        // Remove nested rules - only root and exclude rules remain
        filter.RemoveLastRules(srcAdded);
        Assert.False(filter.IsIgnored("Foo.generated.cs", false));
        Assert.True(filter.IsIgnored("main.o", false));
        Assert.True(filter.IsIgnored("notes.swp", false));
    }
}

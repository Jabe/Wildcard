namespace Wildcard.Tests;

public class GlobFuzzTests : IDisposable
{
    private readonly string _tempDir;

    public GlobFuzzTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wildcard_fuzz_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
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
        File.WriteAllText(fullPath, "content");
    }

    // --- Brace expansion fuzzing ---

    [Fact]
    public void BraceExpander_NeverThrows_RandomPatterns()
    {
        var rng = new Random(42);
        var chars = new[] { '{', '}', ',', '*', '?', '[', ']', '\\', '/', 'a', 'b', '.', ' ' };

        for (int i = 0; i < 1000; i++)
        {
            int len = rng.Next(1, 30);
            var pattern = new char[len];
            for (int j = 0; j < len; j++)
                pattern[j] = chars[rng.Next(chars.Length)];

            var input = new string(pattern);
            var result = BraceExpander.Expand(input);
            Assert.NotNull(result);
            Assert.True(result.Length >= 1, $"Pattern '{input}' produced empty expansion");
        }
    }

    [Fact]
    public void BraceExpander_ExpandedResults_ContainNoBraces()
    {
        // For well-formed brace patterns, results should have no remaining top-level braces
        var patterns = new[]
        {
            "{a,b}", "*.{cs,razor,css}", "{src,lib}/**/*.{cs,json}",
            "{a,{b,{c,d}}}", "x{1,2}y{3,4}z", "{a,b,c,d,e,f}",
            "test.{a,b}.{c,d}", "{,a,b,}", "**/*.{razor,cs,css}"
        };

        foreach (var pattern in patterns)
        {
            var results = BraceExpander.Expand(pattern);
            foreach (var result in results)
            {
                // Should not contain unescaped top-level braces with commas
                // (nested braces are recursively expanded)
                var reExpanded = BraceExpander.Expand(result);
                Assert.Single(reExpanded); // idempotent: already fully expanded
            }
        }
    }

    [Fact]
    public void BraceExpander_RoundTrip_GlobParseSucceeds()
    {
        var patterns = new[]
        {
            "**/*.{cs,razor}", "{src,lib}/**", "*.{a,b,c}",
            "{a,{b,c}}/file.*", "**/{x,y}/*.{cs,json}"
        };

        foreach (var pattern in patterns)
        {
            var expanded = BraceExpander.Expand(pattern);
            foreach (var exp in expanded)
            {
                // Should not throw
                var glob = Glob.Parse(exp);
                Assert.NotNull(glob);
            }
        }
    }

    [Fact]
    public void BraceExpansion_IsMatch_EqualsUnionOfExpansions()
    {
        var rng = new Random(123);
        var patterns = new[]
        {
            "**/*.{cs,md,json}", "{src,docs}/**/*", "*.{a,b,c}",
            "{x,y}/{a,b}.txt", "**/{foo,bar}/*.cs"
        };
        var paths = new[]
        {
            "src/file.cs", "docs/readme.md", "file.json", "file.txt",
            "x/a.txt", "y/b.txt", "z/c.txt", "src/deep/file.cs",
            "foo/test.cs", "bar/test.cs", "baz/test.cs"
        };

        foreach (var pattern in patterns)
        {
            var compositeGlob = Glob.Parse(pattern);
            var expanded = BraceExpander.Expand(pattern);
            var individualGlobs = expanded.Select(Glob.Parse).ToArray();

            foreach (var path in paths)
            {
                bool compositeResult = compositeGlob.IsMatch(path);
                bool unionResult = individualGlobs.Any(g => g.IsMatch(path));
                Assert.Equal(unionResult, compositeResult);
            }
        }
    }

    [Fact]
    public void BraceExpander_AdversarialInputs()
    {
        // All of these should not throw or hang
        var adversarial = new[]
        {
            "{a,{b,{c,{d,{e,{f,g}}}}}}",
            "{,,,,}",
            "\\{a,b\\},{c,d}",
            "*{a,b}?{c,d}[ef]",
            "{}", // empty braces
            "}{", // reversed braces
            "{{{{", // all open
            "}}}}", // all close
            "{a,b{c,d}e,f}",
            string.Join(",", Enumerable.Range(0, 100).Select(i => $"alt{i}")).Insert(0, "{") + "}",
        };

        foreach (var input in adversarial)
        {
            var result = BraceExpander.Expand(input);
            Assert.NotNull(result);
            Assert.True(result.Length >= 1);
        }
    }

    [Fact]
    public void BraceExpander_ExplosionGuard()
    {
        // Many brace groups that would produce huge cartesian product
        // {a,b} x {c,d} x ... = 2^N
        var pattern = string.Concat(Enumerable.Repeat("{a,b}", 20)); // 2^20 = 1M potential
        var result = BraceExpander.Expand(pattern);
        Assert.True(result.Length <= 1024, $"Expansion produced {result.Length} results, expected <= 1024");
    }

    // --- General glob fuzzing ---

    [Fact]
    public void GlobParse_NeverThrows_RandomPatterns()
    {
        var rng = new Random(77);
        var chars = new[] { '*', '?', '[', ']', '{', '}', ',', '/', '\\', '.', 'a', 'z', '0' };

        for (int i = 0; i < 1000; i++)
        {
            int len = rng.Next(1, 40);
            var pattern = new char[len];
            for (int j = 0; j < len; j++)
                pattern[j] = chars[rng.Next(chars.Length)];

            var input = new string(pattern);
            // Should not throw (only ArgumentNullException for null is expected)
            try
            {
                var glob = Glob.Parse(input);
                Assert.NotNull(glob);
            }
            catch (Exception ex) when (ex is not ArgumentNullException)
            {
                Assert.Fail($"Glob.Parse threw {ex.GetType().Name} for pattern '{input}': {ex.Message}");
            }
        }
    }

    [Fact]
    public void GlobIsMatch_NeverThrows_RandomPatternsAndPaths()
    {
        var rng = new Random(88);
        var patternChars = new[] { '*', '?', '[', ']', '{', '}', ',', '/', 'a', 'b', '.' };
        var pathChars = new[] { '/', 'a', 'b', 'c', '.', 'x', 'y' };

        for (int i = 0; i < 500; i++)
        {
            var pattern = RandomString(rng, patternChars, 1, 30);
            var path = RandomString(rng, pathChars, 1, 20);

            try
            {
                var glob = Glob.Parse(pattern);
                // Should not throw
                glob.IsMatch(path);
            }
            catch (Exception ex) when (ex is not ArgumentNullException)
            {
                Assert.Fail($"Glob.IsMatch threw {ex.GetType().Name} for pattern '{pattern}', path '{path}': {ex.Message}");
            }
        }
    }

    [Fact]
    public void FilesystemRoundTrip_AllResultsExist()
    {
        // Create a diverse file tree
        var files = new[]
        {
            "a.cs", "b.md", "c.json",
            "src/x.cs", "src/y.razor", "src/z.css",
            "lib/deep/file.cs", "lib/deep/file.md",
            "docs/readme.txt", "docs/guide.md",
            "data/test file.json", // space in name
            "data/sub/nested.cs",
        };

        foreach (var f in files)
            CreateFile(f);

        var patterns = new[]
        {
            "**/*.cs", "**/*.{cs,md}", "**/*", "{src,lib}/**/*",
            "**/*.{cs,razor,css}", "data/**/*.{json,cs}", "*.*"
        };

        foreach (var pattern in patterns)
        {
            var results = Glob.Match(pattern, _tempDir).ToList();
            foreach (var result in results)
            {
                Assert.True(File.Exists(result), $"Pattern '{pattern}' returned non-existent path: {result}");
            }
        }
    }

    [Fact]
    public void DoubleStarEquivalence()
    {
        CreateFile("a.cs");
        CreateFile("src/b.cs");
        CreateFile("src/deep/c.cs");
        CreateFile("other/d.txt");

        var single = Glob.Match("**/*.cs", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var doubled = Glob.Match("**/**/*.cs", _tempDir)
            .Select(p => Path.GetRelativePath(_tempDir, p))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(single, doubled);
    }

    [Fact]
    public void CharClassFuzzing_NoCrashes()
    {
        var patterns = new[]
        {
            "[abc]*", "[!abc]*", "[a-z]*", "[]]foo", "[\\]]*",
            "[a-zA-Z0-9]*", "[!a-z]*", "[]]*", "[*?]*"
        };

        CreateFile("abc.txt");
        CreateFile("xyz.txt");
        CreateFile("]foo.txt");

        foreach (var pattern in patterns)
        {
            // Should not throw
            var glob = Glob.Parse(pattern);
            var results = glob.EnumerateMatches(_tempDir).ToList();
            Assert.NotNull(results);
        }
    }

    [Theory]
    [InlineData("src/*.cs", "src/*.cs")]
    [InlineData("src\\*.cs", "src/*.cs")]
    public void PathSeparatorNormalization_ConsistentIsMatch(string pattern1, string pattern2)
    {
        var paths = new[] { "src/file.cs", "src\\file.cs", "src/deep/file.cs" };
        var glob1 = Glob.Parse(pattern1);
        var glob2 = Glob.Parse(pattern2);

        foreach (var path in paths)
        {
            Assert.Equal(glob1.IsMatch(path), glob2.IsMatch(path));
        }
    }

    [Fact]
    public void Determinism_SameResultsAcrossRuns()
    {
        CreateFile("a.cs");
        CreateFile("b.cs");
        CreateFile("src/c.cs");
        CreateFile("src/d.md");

        var pattern = "**/*.{cs,md}";
        var run1 = Glob.Match(pattern, _tempDir).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var run2 = Glob.Match(pattern, _tempDir).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var run3 = Glob.Match(pattern, _tempDir).Order(StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(run1, run2);
        Assert.Equal(run2, run3);
    }

    private static string RandomString(Random rng, char[] chars, int minLen, int maxLen)
    {
        int len = rng.Next(minLen, maxLen);
        var buf = new char[len];
        for (int i = 0; i < len; i++)
            buf[i] = chars[rng.Next(chars.Length)];
        return new string(buf);
    }
}

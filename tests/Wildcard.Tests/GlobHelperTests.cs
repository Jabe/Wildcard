namespace Wildcard.Tests;

public class GlobHelperTests
{
    // --- NeedsRecursiveWatch ---

    [Theory]
    [InlineData("*.cs", false)]
    [InlineData("src/*.cs", false)]
    [InlineData("src/*", false)]
    [InlineData("src/**", true)]  // ** is inherently recursive
    [InlineData("**/*.cs", true)]
    [InlineData("*/*.cs", true)]
    [InlineData("src/*/test/*.cs", true)]
    [InlineData("src/**/test", true)]
    [InlineData("a/b/c", false)]
    public void NeedsRecursiveWatch(string pattern, bool expected)
    {
        Assert.Equal(expected, GlobHelper.NeedsRecursiveWatch(pattern));
    }

    [Fact]
    public void NeedsRecursiveWatch_BackslashNormalized()
    {
        Assert.True(GlobHelper.NeedsRecursiveWatch("src\\**\\*.cs"));
    }

    [Fact]
    public void NeedsRecursiveWatch_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => GlobHelper.NeedsRecursiveWatch(null!));
    }

    [Fact]
    public void NeedsRecursiveWatch_ThrowsOnEmpty()
    {
        Assert.Throws<ArgumentException>(() => GlobHelper.NeedsRecursiveWatch(""));
    }

    // --- GetWatchBaseDirectory ---

    // Use a platform-appropriate rooted path for cwd (Windows needs a drive letter)
    private static readonly string TestRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wctest"));

    [Theory]
    [InlineData("*.cs")]
    [InlineData("**/*.cs")]
    public void GetWatchBaseDirectory_NoLiteralPrefix_ReturnsCwd(string pattern)
    {
        Assert.Equal(TestRoot, GlobHelper.GetWatchBaseDirectory(pattern, TestRoot));
    }

    [Theory]
    [InlineData("src/**/*.cs", "src")]
    [InlineData("src/lib/**", "src/lib")]
    [InlineData("a/b/c/*.txt", "a/b/c")]
    public void GetWatchBaseDirectory_ExtractsLiteralPrefix(string pattern, string expectedSuffix)
    {
        var result = GlobHelper.GetWatchBaseDirectory(pattern, TestRoot);
        var expected = Path.GetFullPath(Path.Combine(TestRoot, expectedSuffix));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetWatchBaseDirectory_BackslashNormalized()
    {
        var result = GlobHelper.GetWatchBaseDirectory("src\\lib\\**\\*.cs", TestRoot);
        var expected = Path.GetFullPath(Path.Combine(TestRoot, "src", "lib"));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetWatchBaseDirectory_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => GlobHelper.GetWatchBaseDirectory(null!, TestRoot));
    }

    [Fact]
    public void GetWatchBaseDirectory_ThrowsOnEmpty()
    {
        Assert.Throws<ArgumentException>(() => GlobHelper.GetWatchBaseDirectory("", TestRoot));
    }
}

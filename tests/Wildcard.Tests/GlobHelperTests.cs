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

    [Theory]
    [InlineData("*.cs", "/tmp", "/tmp")]
    [InlineData("src/**/*.cs", "/tmp", "/tmp/src")]
    [InlineData("src/lib/**", "/tmp", "/tmp/src/lib")]
    [InlineData("**/*.cs", "/tmp", "/tmp")]
    [InlineData("a/b/c/*.txt", "/tmp", "/tmp/a/b/c")]
    public void GetWatchBaseDirectory(string pattern, string cwd, string expected)
    {
        Assert.Equal(expected, GlobHelper.GetWatchBaseDirectory(pattern, cwd));
    }

    [Fact]
    public void GetWatchBaseDirectory_AbsolutePattern()
    {
        var result = GlobHelper.GetWatchBaseDirectory("/usr/local/**/*.log", "/ignored");
        Assert.Equal("/usr/local", result);
    }

    [Fact]
    public void GetWatchBaseDirectory_NoLiteralPrefix_ReturnsCwd()
    {
        Assert.Equal("/work", GlobHelper.GetWatchBaseDirectory("**", "/work"));
    }

    [Fact]
    public void GetWatchBaseDirectory_BackslashNormalized()
    {
        Assert.Equal("/tmp/src/lib", GlobHelper.GetWatchBaseDirectory("src\\lib\\**\\*.cs", "/tmp"));
    }

    [Fact]
    public void GetWatchBaseDirectory_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => GlobHelper.GetWatchBaseDirectory(null!, "/tmp"));
    }

    [Fact]
    public void GetWatchBaseDirectory_ThrowsOnEmpty()
    {
        Assert.Throws<ArgumentException>(() => GlobHelper.GetWatchBaseDirectory("", "/tmp"));
    }
}

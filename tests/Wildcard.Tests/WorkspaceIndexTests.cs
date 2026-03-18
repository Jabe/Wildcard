using Wildcard.Mcp;

namespace Wildcard.Tests;

public class WorkspaceIndexTests : IDisposable
{
    private readonly string _tempDir;

    public WorkspaceIndexTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WorkspaceIndexTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                Assert.Fail("Timed out waiting for condition");
            await Task.Delay(100);
        }
    }

    private void CreateFile(string relativePath, string content = "")
    {
        var fullPath = Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    [Fact]
    public async Task InitialScan_PopulatesFiles()
    {
        CreateFile("a.txt");
        CreateFile("src/b.cs");
        CreateFile("src/nested/c.cs");

        using var index = new WorkspaceIndex(_tempDir);
        await index.WaitForReady();

        var files = index.MatchGlob("**/*", _tempDir, null).ToList();
        Assert.Equal(3, files.Count);
    }

    [Fact]
    public async Task MatchGlob_FiltersCorrectly()
    {
        CreateFile("a.txt");
        CreateFile("b.cs");
        CreateFile("src/c.cs");
        CreateFile("src/d.txt");

        using var index = new WorkspaceIndex(_tempDir);
        await index.WaitForReady();

        var csFiles = index.MatchGlob("**/*.cs", _tempDir, null).ToList();
        Assert.Equal(2, csFiles.Count);
        Assert.All(csFiles, f => Assert.EndsWith(".cs", f));
    }

    [Fact]
    public async Task MatchGlob_ExcludePaths()
    {
        CreateFile("a.cs");
        CreateFile("src/b.cs");
        CreateFile("vendor/c.cs");

        using var index = new WorkspaceIndex(_tempDir);
        await index.WaitForReady();

        var files = index.MatchGlob("**/*.cs", _tempDir, ["vendor/**"]).ToList();
        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(files, f => f.Contains("vendor"));
    }

    [Fact]
    public async Task MatchGlob_SubBaseDirectory()
    {
        CreateFile("src/a.cs");
        CreateFile("src/sub/b.cs");
        CreateFile("other/c.cs");

        using var index = new WorkspaceIndex(_tempDir);
        await index.WaitForReady();

        var srcDir = Path.Combine(_tempDir, "src");
        var files = index.MatchGlob("**/*.cs", srcDir, null).ToList();
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.StartsWith(srcDir, f));
    }

    [Fact]
    public async Task MatchGlob_ReturnsAbsolutePaths()
    {
        CreateFile("hello.txt");

        using var index = new WorkspaceIndex(_tempDir);
        await index.WaitForReady();

        var files = index.MatchGlob("**/*", _tempDir, null).ToList();
        Assert.Single(files);
        Assert.True(Path.IsPathRooted(files[0]));
    }

    [Fact]
    public async Task NotifyFileWritten_AddsPath()
    {
        CreateFile("a.txt");

        using var index = new WorkspaceIndex(_tempDir);
        await index.WaitForReady();

        Assert.Single(index.MatchGlob("**/*", _tempDir, null).ToList());

        // Create new file and notify
        CreateFile("b.txt");
        index.NotifyFileWritten(Path.Combine(_tempDir, "b.txt"));

        Assert.Equal(2, index.MatchGlob("**/*", _tempDir, null).Count());
    }

    [Fact]
    public async Task NotifyFileDeleted_RemovesPath()
    {
        CreateFile("a.txt");
        CreateFile("b.txt");

        using var index = new WorkspaceIndex(_tempDir);
        await index.WaitForReady();

        Assert.Equal(2, index.MatchGlob("**/*", _tempDir, null).Count());

        index.NotifyFileDeleted(Path.Combine(_tempDir, "b.txt"));

        Assert.Single(index.MatchGlob("**/*", _tempDir, null).ToList());
    }

    [Fact]
    public async Task NotifyFileWritten_IgnoresPathsOutsideRoot()
    {
        CreateFile("a.txt");

        using var index = new WorkspaceIndex(_tempDir);
        await index.WaitForReady();

        index.NotifyFileWritten("/some/other/path/file.txt");

        Assert.Single(index.MatchGlob("**/*", _tempDir, null).ToList());
    }

    [Fact]
    public async Task FSW_DetectsNewFile()
    {
        CreateFile("a.txt");

        using var index = new WorkspaceIndex(_tempDir);
        await index.WaitForReady();

        Assert.Single(index.MatchGlob("**/*", _tempDir, null).ToList());

        // Create a new file and wait for FSW + debounce
        CreateFile("b.txt", "new file");
        await WaitUntilAsync(() => index.MatchGlob("**/*", _tempDir, null).Count() == 2, TimeSpan.FromSeconds(5));

        Assert.Equal(2, index.MatchGlob("**/*", _tempDir, null).Count());
    }

    [Fact]
    public async Task FSW_DetectsDeletedFile()
    {
        CreateFile("a.txt");
        CreateFile("b.txt");

        using var index = new WorkspaceIndex(_tempDir);
        await index.WaitForReady();

        Assert.Equal(2, index.MatchGlob("**/*", _tempDir, null).Count());

        File.Delete(Path.Combine(_tempDir, "b.txt"));
        await WaitUntilAsync(() => index.MatchGlob("**/*", _tempDir, null).Count() == 1, TimeSpan.FromSeconds(5));

        Assert.Single(index.MatchGlob("**/*", _tempDir, null).ToList());
    }

    [Fact]
    public async Task FSW_DetectsRenamedFile()
    {
        CreateFile("old.txt");

        using var index = new WorkspaceIndex(_tempDir);
        await index.WaitForReady();

        var oldPath = Path.Combine(_tempDir, "old.txt");
        var newPath = Path.Combine(_tempDir, "new.txt");
        File.Move(oldPath, newPath);
        await WaitUntilAsync(() => index.MatchGlob("**/*", _tempDir, null).Any(f => f.Contains("new.txt")), TimeSpan.FromSeconds(5));

        var files = index.MatchGlob("**/*", _tempDir, null).ToList();
        Assert.Single(files);
        Assert.Contains("new.txt", files[0]);
        Assert.DoesNotContain(files, f => f.Contains("old.txt"));
    }

    [Fact]
    public async Task WaitForReady_CompletesAfterScan()
    {
        // Create several files to make scan non-trivial
        for (int i = 0; i < 50; i++)
            CreateFile($"dir{i / 10}/file{i}.txt");

        using var index = new WorkspaceIndex(_tempDir);

        var readyTask = index.WaitForReady();
        await readyTask;

        // After ready, all files should be available
        var files = index.MatchGlob("**/*.txt", _tempDir, null).ToList();
        Assert.Equal(50, files.Count);
    }
}

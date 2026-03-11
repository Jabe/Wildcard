using System.Collections.Concurrent;
using System.Diagnostics;

namespace Wildcard.Tests;

// Run sequentially — each test launches a CLI process; parallel runs cause MSBuild lock contention
[CollectionDefinition("WatchMode", DisableParallelization = true)]
public class WatchModeCollection;

[Collection("WatchMode")]
[Trait("Category", "Integration")]
public class WatchModeIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    private static readonly string CliProjectPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Wildcard.Cli", "Wildcard.Cli.csproj"));

    private static readonly string CliDllPath;

    static WatchModeIntegrationTests()
    {
        // Pre-build the CLI once for all tests
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(CliProjectPath);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("q");
        var p = Process.Start(psi)!;
        p.WaitForExit(30000);

        // Resolve the built DLL path
        var projDir = Path.GetDirectoryName(CliProjectPath)!;
        CliDllPath = Path.Combine(projDir, "bin", "Debug", "net10.0", "wcg.dll");
    }

    public WatchModeIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wildcard_watch_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task FileListWatch_ShowsNewFiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.cs"), "class A {}");

        using var proc = StartWcg("*.cs", "--watch", "--no-ignore");
        var lines = CaptureOutput(proc);
        try
        {
            await WaitFor(() => lines.Any(l => l.Contains("a.cs")));

            var bPath = Path.Combine(_tempDir, "b.cs");
            File.WriteAllText(bPath, "class B {}");
            // Touch file to ensure FSW fires Changed event (macOS kqueue reliability)
            await Task.Delay(200);
            File.SetLastWriteTimeUtc(bPath, DateTime.UtcNow);

            await WaitFor(() => lines.Any(l => l.Contains("b.cs")));
        }
        finally
        {
            Kill(proc);
        }
    }

    [Fact]
    public async Task ContentSearchWatch_ShowsNewMatches()
    {
        File.WriteAllText(Path.Combine(_tempDir, "app.log"), "INFO started\n");

        using var proc = StartWcg("*.log", "*ERROR*", "--watch", "--no-ignore");
        var lines = CaptureOutput(proc);
        try
        {
            // Wait for initial scan to complete (watch message on stderr)
            await Task.Delay(1500);

            var logPath = Path.Combine(_tempDir, "app.log");
            File.AppendAllText(logPath, "ERROR boom\n");
            // Touch file to ensure FSW fires Changed event (macOS kqueue reliability)
            await Task.Delay(200);
            File.SetLastWriteTimeUtc(logPath, DateTime.UtcNow);

            await WaitFor(() => lines.Any(l => l.Contains("ERROR boom")));
        }
        finally
        {
            Kill(proc);
        }
    }

    [Fact]
    public async Task ContentSearchWatch_MultisetDedup_ReportsNewDuplicates()
    {
        File.WriteAllText(Path.Combine(_tempDir, "data.txt"), "hello world\nhello world\n");

        using var proc = StartWcg("*.txt", "*hello*", "--watch", "--no-ignore");
        var lines = CaptureOutput(proc);
        try
        {
            // Wait for initial scan (2 matches shown)
            await WaitFor(() => lines.Count(l => l.Contains("hello world")) >= 2);
            int countBefore = lines.Count(l => l.Contains("hello world"));

            var dataPath = Path.Combine(_tempDir, "data.txt");
            File.AppendAllText(dataPath, "hello world\n");
            // Touch file to ensure FSW fires Changed event (macOS kqueue reliability)
            await Task.Delay(200);
            File.SetLastWriteTimeUtc(dataPath, DateTime.UtcNow);

            // Should see exactly 1 new match (multiset diff: 3 - 2 = 1)
            await WaitFor(() => lines.Count(l => l.Contains("hello world")) > countBefore);
            int countAfter = lines.Count(l => l.Contains("hello world"));
            Assert.Equal(countBefore + 1, countAfter);
        }
        finally
        {
            Kill(proc);
        }
    }

    [Fact]
    public async Task FileListWatch_RespectsGitignore()
    {
        // Init a git repo so gitignore is active
        RunGit("init");
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(_tempDir, "a.cs"), "class A {}");

        using var proc = StartWcg("**/*", "--watch");
        var lines = CaptureOutput(proc);
        try
        {
            await WaitFor(() => lines.Any(l => l.Contains("a.cs")));

            // Create an ignored file
            File.WriteAllText(Path.Combine(_tempDir, "debug.log"), "some log data");

            // Wait a bit — the ignored file should NOT appear
            await Task.Delay(2000);
            Assert.DoesNotContain(lines, l => l.Contains("debug.log"));
        }
        finally
        {
            Kill(proc);
        }
    }

    [Fact]
    public async Task DirectoryWildcard_WatchesSubdirectories()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "sub"));
        File.WriteAllText(Path.Combine(_tempDir, "sub", "a.cs"), "class A {}");

        using var proc = StartWcg("*/*.cs", "--watch", "--no-ignore");
        var lines = CaptureOutput(proc);
        try
        {
            await WaitFor(() => lines.Any(l => l.Contains("a.cs")));

            var bPath = Path.Combine(_tempDir, "sub", "b.cs");
            File.WriteAllText(bPath, "class B {}");
            // Touch file to ensure FSW fires Changed event (macOS kqueue reliability)
            await Task.Delay(200);
            File.SetLastWriteTimeUtc(bPath, DateTime.UtcNow);

            await WaitFor(() => lines.Any(l => l.Contains("b.cs")));
        }
        finally
        {
            Kill(proc);
        }
    }

    // --- Helpers ---

    private Process StartWcg(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _tempDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { ["NO_COLOR"] = "1" },
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(CliProjectPath);
        psi.ArgumentList.Add("--");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var proc = Process.Start(psi)!;
        return proc;
    }

    private static ConcurrentBag<string> CaptureOutput(Process proc)
    {
        var lines = new ConcurrentBag<string>();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lines.Add(e.Data); };
        proc.BeginOutputReadLine();
        // Also drain stderr to prevent buffer deadlock
        proc.ErrorDataReceived += (_, _) => { };
        proc.BeginErrorReadLine();
        return lines;
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(100);
        Assert.True(condition(), $"Condition not met within {timeoutMs}ms");
    }

    private static void Kill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); } catch { }
        proc.WaitForExit(3000);
    }

    private void RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _tempDir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        p.WaitForExit(5000);
    }
}

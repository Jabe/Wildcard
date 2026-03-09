using System.Text;
using BenchmarkDotNet.Attributes;

namespace Wildcard.Benchmarks;

/// <summary>
/// Benchmarks file content scanning — baseline (ReadAllLines + FilterLines) vs FilePathMatcher.
/// Generates temp files on disk with realistic content.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class FilePathMatcherBenchmarks
{
    private string _tempDir = null!;
    private string[] _filePaths = null!;
    private WildcardPattern _pattern = null!;
    private FilePathMatcher _matcher = null!;

    [Params("small", "medium", "large")]
    public string FileSize { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        _matcher = FilePathMatcher.Create("*ERROR*");
        _tempDir = Path.Combine(Path.GetTempPath(), $"wildcard_bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _pattern = WildcardPattern.Compile("*ERROR*");

        int lineCount = FileSize switch
        {
            "small" => 1_000,
            "medium" => 100_000,
            "large" => 1_000_000,
            _ => 10_000
        };

        // Generate 4 files with ~12.5% matching lines
        _filePaths = new string[4];
        var rng = new Random(42);
        var logLevels = new[] { "INFO", "DEBUG", "WARN", "ERROR", "TRACE", "INFO", "DEBUG", "INFO" };

        for (int f = 0; f < 4; f++)
        {
            var path = Path.Combine(_tempDir, $"logfile_{f}.log");
            _filePaths[f] = path;

            using var writer = new StreamWriter(path, false, new UTF8Encoding(false), 65536);
            for (int i = 0; i < lineCount; i++)
            {
                var level = logLevels[rng.Next(logLevels.Length)];
                writer.Write($"[2024-03-15 14:{i % 60:D2}:{i % 60:D2}] {level}    ");
                writer.Write($"Service operation completed: request_id={rng.Next(100000):D5} ");
                writer.WriteLine($"duration={rng.Next(1, 5000)}ms status=ok module=payments");
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Benchmark(Baseline = true, Description = "Baseline: ReadAllLines + FilterLines")]
    public int Baseline_ReadAllLines_FilterLines()
    {
        int totalMatches = 0;
        foreach (var filePath in _filePaths)
        {
            var lines = File.ReadAllLines(filePath);
            var matches = WildcardSearch.FilterLines(_pattern, lines);
            totalMatches += matches.Count;
        }
        return totalMatches;
    }

    [Benchmark(Description = "Baseline: ReadLines (lazy) + FilterLines")]
    public int Baseline_ReadLines_FilterLines()
    {
        int totalMatches = 0;
        foreach (var filePath in _filePaths)
        {
            var lines = File.ReadLines(filePath);
            var matches = WildcardSearch.FilterLines(_pattern, lines);
            totalMatches += matches.Count;
        }
        return totalMatches;
    }

    [Benchmark(Description = "FilePathMatcher.Scan (mmap + parallel)")]
    public int FilePathMatcher_Scan()
    {
        return _matcher.Scan(_filePaths).Count;
    }
}

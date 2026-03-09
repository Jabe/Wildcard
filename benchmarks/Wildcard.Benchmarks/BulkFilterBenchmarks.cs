using System.IO.Enumeration;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;

namespace Wildcard.Benchmarks;

/// <summary>
/// Benchmarks bulk filtering of string arrays — Wildcard vs Regex vs FileSystemName.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class BulkFilterBenchmarks
{
    private string[] _inputs = null!;
    private WildcardPattern _wcPattern = null!;
    private Regex _rxPattern = null!;

    [Params(100, 10_000)]
    public int N;

    [GlobalSetup]
    public void Setup()
    {
        var extensions = new[] { ".cs", ".csv", ".json", ".xml", ".md", ".txt", ".log", ".yaml" };
        var rng = new Random(42);

        _inputs = new string[N];
        for (int i = 0; i < N; i++)
        {
            var ext = extensions[rng.Next(extensions.Length)];
            _inputs[i] = $"file_{i:D6}{ext}";
        }

        _wcPattern = WildcardPattern.Compile("*.cs");
        _rxPattern = new Regex(@"^.*\.cs$", RegexOptions.Compiled);
    }

    [Benchmark(Description = "Wildcard  FilterLines")]
    public List<string> Wildcard_Filter()
        => WildcardSearch.FilterLines(_wcPattern, _inputs);

    [Benchmark(Description = "Wildcard  FilterBulk (parallel)")]
    public string[] Wildcard_FilterBulk()
        => WildcardSearch.FilterBulk(_wcPattern, _inputs, parallel: true);

    [Benchmark(Description = "Regex     LINQ filter")]
    public string[] Regex_Filter()
        => _inputs.Where(s => _rxPattern.IsMatch(s)).ToArray();

    [Benchmark(Description = "FSName    LINQ filter")]
    public string[] FSName_Filter()
        => _inputs.Where(s => FileSystemName.MatchesSimpleExpression("*.cs", s)).ToArray();
}

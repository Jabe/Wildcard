using System.IO.Enumeration;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;

namespace Wildcard.Benchmarks;

/// <summary>
/// Compares Wildcard vs Regex vs FileSystemName.MatchesSimpleExpression
/// across several common pattern shapes.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class PatternMatchBenchmarks
{
    // --- Inputs ---

    private const string ShortInput = "report_2024.csv";
    private const string LongInput = "src/modules/analytics/reports/quarterly/report_2024_q3_final_revised_v2.csv";
    private const string NoMatchInput = "readme.md";

    // --- Patterns ---

    // Simple extension match: *.csv
    private WildcardPattern _wcExtension = null!;
    private Regex _rxExtension = null!;

    // Prefix + suffix: report*csv
    private WildcardPattern _wcPrefixSuffix = null!;
    private Regex _rxPrefixSuffix = null!;

    // Single-char wildcard: report_????.csv
    private WildcardPattern _wcQuestion = null!;
    private Regex _rxQuestion = null!;

    // Character class: [rs]*.*
    private WildcardPattern _wcCharClass = null!;
    private Regex _rxCharClass = null!;

    // Multiple stars: *report*2024*
    private WildcardPattern _wcMultiStar = null!;
    private Regex _rxMultiStar = null!;

    [GlobalSetup]
    public void Setup()
    {
        _wcExtension = WildcardPattern.Compile("*.csv");
        _rxExtension = new Regex(@"^.*\.csv$", RegexOptions.Compiled);

        _wcPrefixSuffix = WildcardPattern.Compile("report*.csv");
        _rxPrefixSuffix = new Regex(@"^report.*\.csv$", RegexOptions.Compiled);

        _wcQuestion = WildcardPattern.Compile("report_????.csv");
        _rxQuestion = new Regex(@"^report_.{4}\.csv$", RegexOptions.Compiled);

        _wcCharClass = WildcardPattern.Compile("[rs]*.*");
        _rxCharClass = new Regex(@"^[rs].*\..*$", RegexOptions.Compiled);

        _wcMultiStar = WildcardPattern.Compile("*report*2024*");
        _rxMultiStar = new Regex(@"^.*report.*2024.*$", RegexOptions.Compiled);
    }

    // ==================== Extension match: *.csv ====================

    [Benchmark(Description = "Wildcard  *.csv (short)")]
    public bool Wildcard_Extension_Short() => _wcExtension.IsMatch(ShortInput);

    [Benchmark(Description = "Regex     *.csv (short)")]
    public bool Regex_Extension_Short() => _rxExtension.IsMatch(ShortInput);

    [Benchmark(Description = "FSName    *.csv (short)")]
    public bool FSName_Extension_Short() => FileSystemName.MatchesSimpleExpression("*.csv", ShortInput);

    [Benchmark(Description = "Wildcard  *.csv (long)")]
    public bool Wildcard_Extension_Long() => _wcExtension.IsMatch(LongInput);

    [Benchmark(Description = "Regex     *.csv (long)")]
    public bool Regex_Extension_Long() => _rxExtension.IsMatch(LongInput);

    [Benchmark(Description = "FSName    *.csv (long)")]
    public bool FSName_Extension_Long() => FileSystemName.MatchesSimpleExpression("*.csv", LongInput);

    // ==================== Prefix + suffix: report*.csv ====================

    [Benchmark(Description = "Wildcard  report*.csv")]
    public bool Wildcard_PrefixSuffix() => _wcPrefixSuffix.IsMatch(ShortInput);

    [Benchmark(Description = "Regex     report*.csv")]
    public bool Regex_PrefixSuffix() => _rxPrefixSuffix.IsMatch(ShortInput);

    [Benchmark(Description = "FSName    report*.csv")]
    public bool FSName_PrefixSuffix() => FileSystemName.MatchesSimpleExpression("report*.csv", ShortInput);

    // ==================== Question marks: report_????.csv ====================

    [Benchmark(Description = "Wildcard  report_????.csv")]
    public bool Wildcard_Question() => _wcQuestion.IsMatch(ShortInput);

    [Benchmark(Description = "Regex     report_????.csv")]
    public bool Regex_Question() => _rxQuestion.IsMatch(ShortInput);

    // FileSystemName supports ? so include it
    [Benchmark(Description = "FSName    report_????.csv")]
    public bool FSName_Question() => FileSystemName.MatchesSimpleExpression("report_????.csv", ShortInput);

    // ==================== Character class: [rs]*.* ====================

    [Benchmark(Description = "Wildcard  [rs]*.*")]
    public bool Wildcard_CharClass() => _wcCharClass.IsMatch(ShortInput);

    [Benchmark(Description = "Regex     [rs]*.*")]
    public bool Regex_CharClass() => _rxCharClass.IsMatch(ShortInput);

    // FileSystemName doesn't support character classes — skip

    // ==================== Multiple stars: *report*2024* ====================

    [Benchmark(Description = "Wildcard  *report*2024* (short)")]
    public bool Wildcard_MultiStar_Short() => _wcMultiStar.IsMatch(ShortInput);

    [Benchmark(Description = "Regex     *report*2024* (short)")]
    public bool Regex_MultiStar_Short() => _rxMultiStar.IsMatch(ShortInput);

    [Benchmark(Description = "FSName    *report*2024* (short)")]
    public bool FSName_MultiStar_Short() => FileSystemName.MatchesSimpleExpression("*report*2024*", ShortInput);

    [Benchmark(Description = "Wildcard  *report*2024* (long)")]
    public bool Wildcard_MultiStar_Long() => _wcMultiStar.IsMatch(LongInput);

    [Benchmark(Description = "Regex     *report*2024* (long)")]
    public bool Regex_MultiStar_Long() => _rxMultiStar.IsMatch(LongInput);

    [Benchmark(Description = "FSName    *report*2024* (long)")]
    public bool FSName_MultiStar_Long() => FileSystemName.MatchesSimpleExpression("*report*2024*", LongInput);

    // ==================== No-match scenario ====================

    [Benchmark(Description = "Wildcard  *.csv (no match)")]
    public bool Wildcard_NoMatch() => _wcExtension.IsMatch(NoMatchInput);

    [Benchmark(Description = "Regex     *.csv (no match)")]
    public bool Regex_NoMatch() => _rxExtension.IsMatch(NoMatchInput);

    [Benchmark(Description = "FSName    *.csv (no match)")]
    public bool FSName_NoMatch() => FileSystemName.MatchesSimpleExpression("*.csv", NoMatchInput);
}

using System.IO.Enumeration;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;

namespace Wildcard.Benchmarks;

/// <summary>
/// General-purpose string matching benchmarks — names, emails, log lines, product codes.
/// These reflect real-world use cases beyond filename globbing.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class GeneralMatchBenchmarks
{
    // ── Inputs ─────────────────────────────────────────────────────────────

    // Names
    private const string FullName        = "Jonathan Alexander Smith";
    private const string NoMatchName     = "Maria García López";

    // Emails
    private const string EmailGmail      = "john.doe@gmail.com";
    private const string EmailCorp       = "j.smith@internal.acme-corp.com";

    // Log lines
    private const string LogError        = "[2024-03-15 14:22:01] ERROR   Payment service timeout after 30s";
    private const string LogInfo         = "[2024-03-15 14:22:01] INFO    User login successful: user_id=4821";

    // Product / SKU codes
    private const string SkuMatch        = "SKU-042-BLUE-XL";
    private const string SkuNoMatch      = "SKU-042-RED-XL";

    // Version strings
    private const string Version         = "v2.11.3-preview.4+build.987";

    // ── Compiled patterns ───────────────────────────────────────────────────

    // "First name starts with J, last name ends with Smith"
    private WildcardPattern _wcNamePrefix = null!;
    private Regex           _rxNamePrefix = null!;

    // "Any Gmail address"
    private WildcardPattern _wcGmail = null!;
    private Regex           _rxGmail = null!;

    // "Corporate email on any subdomain of acme-corp.com"
    private WildcardPattern _wcCorpEmail = null!;
    private Regex           _rxCorpEmail = null!;

    // "Log line containing ERROR and timeout"
    private WildcardPattern _wcLogError = null!;
    private Regex           _rxLogError = null!;

    // "Any log line for a specific date"
    private WildcardPattern _wcLogDate = null!;
    private Regex           _rxLogDate = null!;

    // "SKU with colour BLUE, any size"
    private WildcardPattern _wcSku = null!;
    private Regex           _rxSku = null!;

    // "Version 2.x.x"
    private WildcardPattern _wcVersion = null!;
    private Regex           _rxVersion = null!;

    // "Name with exactly 3-char first name"
    private WildcardPattern _wcNameQuestion = null!;
    private Regex           _rxNameQuestion = null!;

    // "Name starting with a vowel (char class)"
    private WildcardPattern _wcNameCharClass = null!;
    private Regex           _rxNameCharClass = null!;

    [GlobalSetup]
    public void Setup()
    {
        _wcNamePrefix    = WildcardPattern.Compile("J* *Smith",       ignoreCase: true);
        _rxNamePrefix    = new Regex(@"^J\S*\s.*Smith$",              RegexOptions.Compiled | RegexOptions.IgnoreCase);

        _wcGmail         = WildcardPattern.Compile("*@gmail.com");
        _rxGmail         = new Regex(@"^[^@]+@gmail\.com$",           RegexOptions.Compiled);

        _wcCorpEmail     = WildcardPattern.Compile("*@*.acme-corp.com");
        _rxCorpEmail     = new Regex(@"^[^@]+@.+\.acme-corp\.com$",   RegexOptions.Compiled);

        _wcLogError      = WildcardPattern.Compile("*ERROR*timeout*");
        _rxLogError      = new Regex(@"ERROR.*timeout",                RegexOptions.Compiled);

        _wcLogDate       = WildcardPattern.Compile("[[]2024-03-15*");
        _rxLogDate       = new Regex(@"^\[2024-03-15",                 RegexOptions.Compiled);

        _wcSku           = WildcardPattern.Compile("SKU-*-BLUE-*");
        _rxSku           = new Regex(@"^SKU-[^-]+-BLUE-",             RegexOptions.Compiled);

        _wcVersion       = WildcardPattern.Compile("v2.*");
        _rxVersion       = new Regex(@"^v2\.",                         RegexOptions.Compiled);

        _wcNameQuestion  = WildcardPattern.Compile("??? *");
        _rxNameQuestion  = new Regex(@"^.{3}\s",                      RegexOptions.Compiled);

        _wcNameCharClass = WildcardPattern.Compile("[AEIOU]*", ignoreCase: true);
        _rxNameCharClass = new Regex(@"^[aeiou]",                      RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    // ── Name matching ──────────────────────────────────────────────────────

    [Benchmark(Description = "Wildcard  J* *Smith  (match)")]
    public bool Wildcard_Name_Match()   => _wcNamePrefix.IsMatch(FullName);

    [Benchmark(Description = "Regex     J* *Smith  (match)")]
    public bool Regex_Name_Match()      => _rxNamePrefix.IsMatch(FullName);

    [Benchmark(Description = "Wildcard  J* *Smith  (no match)")]
    public bool Wildcard_Name_NoMatch() => _wcNamePrefix.IsMatch(NoMatchName);

    [Benchmark(Description = "Regex     J* *Smith  (no match)")]
    public bool Regex_Name_NoMatch()    => _rxNamePrefix.IsMatch(NoMatchName);

    [Benchmark(Description = "Wildcard  ??? *      (3-char first name)")]
    public bool Wildcard_Name_Question() => _wcNameQuestion.IsMatch(FullName);

    [Benchmark(Description = "Regex     ??? *      (3-char first name)")]
    public bool Regex_Name_Question()    => _rxNameQuestion.IsMatch(FullName);

    [Benchmark(Description = "Wildcard  [AEIOU]*   (starts with vowel)")]
    public bool Wildcard_Name_CharClass() => _wcNameCharClass.IsMatch(NoMatchName);

    [Benchmark(Description = "Regex     [AEIOU]*   (starts with vowel)")]
    public bool Regex_Name_CharClass()    => _rxNameCharClass.IsMatch(NoMatchName);

    // ── Email matching ─────────────────────────────────────────────────────

    [Benchmark(Description = "Wildcard  *@gmail.com")]
    public bool Wildcard_Email_Gmail()   => _wcGmail.IsMatch(EmailGmail);

    [Benchmark(Description = "Regex     *@gmail.com")]
    public bool Regex_Email_Gmail()      => _rxGmail.IsMatch(EmailGmail);

    [Benchmark(Description = "Wildcard  *@*.acme-corp.com")]
    public bool Wildcard_Email_Corp()    => _wcCorpEmail.IsMatch(EmailCorp);

    [Benchmark(Description = "Regex     *@*.acme-corp.com")]
    public bool Regex_Email_Corp()       => _rxCorpEmail.IsMatch(EmailCorp);

    // ── Log line matching ──────────────────────────────────────────────────

    [Benchmark(Description = "Wildcard  *ERROR*timeout*  (match)")]
    public bool Wildcard_Log_Error()     => _wcLogError.IsMatch(LogError);

    [Benchmark(Description = "Regex     *ERROR*timeout*  (match)")]
    public bool Regex_Log_Error()        => _rxLogError.IsMatch(LogError);

    [Benchmark(Description = "Wildcard  *ERROR*timeout*  (no match)")]
    public bool Wildcard_Log_NoError()   => _wcLogError.IsMatch(LogInfo);

    [Benchmark(Description = "Regex     *ERROR*timeout*  (no match)")]
    public bool Regex_Log_NoError()      => _rxLogError.IsMatch(LogInfo);

    [Benchmark(Description = "Wildcard  [[]2024-03-15*   (date prefix)")]
    public bool Wildcard_Log_Date()      => _wcLogDate.IsMatch(LogError);

    [Benchmark(Description = "Regex     [[]2024-03-15*   (date prefix)")]
    public bool Regex_Log_Date()         => _rxLogDate.IsMatch(LogError);

    // ── Product code matching ──────────────────────────────────────────────

    [Benchmark(Description = "Wildcard  SKU-*-BLUE-*  (match)")]
    public bool Wildcard_Sku_Match()     => _wcSku.IsMatch(SkuMatch);

    [Benchmark(Description = "Regex     SKU-*-BLUE-*  (match)")]
    public bool Regex_Sku_Match()        => _rxSku.IsMatch(SkuMatch);

    [Benchmark(Description = "Wildcard  SKU-*-BLUE-*  (no match)")]
    public bool Wildcard_Sku_NoMatch()   => _wcSku.IsMatch(SkuNoMatch);

    [Benchmark(Description = "Regex     SKU-*-BLUE-*  (no match)")]
    public bool Regex_Sku_NoMatch()      => _rxSku.IsMatch(SkuNoMatch);

    // ── Version string matching ────────────────────────────────────────────

    [Benchmark(Description = "Wildcard  v2.*")]
    public bool Wildcard_Version()       => _wcVersion.IsMatch(Version);

    [Benchmark(Description = "Regex     v2.*")]
    public bool Regex_Version()          => _rxVersion.IsMatch(Version);
}

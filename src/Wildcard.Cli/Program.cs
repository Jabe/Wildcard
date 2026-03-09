using Wildcard;

var (parsed, exitCode) = ParseArgs(args);
if (parsed is null)
    return exitCode;

bool useColor = !Console.IsOutputRedirected;
var cwd = Directory.GetCurrentDirectory();
bool anyOutput = false;

// No content pattern — just list files as they're found
if (parsed.ContentPattern is null)
{
    foreach (var file in Glob.Match(parsed.GlobPattern))
    {
        Console.WriteLine(Path.GetRelativePath(cwd, file));
        anyOutput = true;
    }
    return anyOutput ? 0 : 1;
}

// Content search — scan and print per-file for streaming output
var matcher = FilePathMatcher.Create(
    include: [parsed.ContentPattern],
    exclude: parsed.ExcludePatterns.Count > 0 ? parsed.ExcludePatterns.ToArray() : null,
    options: parsed.IgnoreCase ? new FilePathMatcher.Options { IgnoreCase = true } : null
);

string? highlightLiteral = ExtractHighlightLiteral(parsed.ContentPattern, parsed.IgnoreCase);

foreach (var file in Glob.Match(parsed.GlobPattern))
{
    var fileMatches = matcher.Scan(file);
    if (fileMatches.Count == 0) continue;

    if (anyOutput)
        Console.WriteLine();
    anyOutput = true;

    var relPath = Path.GetRelativePath(cwd, file);
    if (useColor)
        Console.WriteLine($"\x1b[35m{relPath}\x1b[0m");
    else
        Console.WriteLine(relPath);

    int width = fileMatches[^1].LineNumber.ToString().Length;

    foreach (var match in fileMatches)
    {
        var lineNum = match.LineNumber.ToString().PadLeft(width);

        if (useColor)
        {
            Console.Write($"  \x1b[32m{lineNum}\x1b[0m\x1b[36m:\x1b[0m ");
            WriteHighlighted(match.Line, highlightLiteral, parsed.IgnoreCase);
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine($"  {lineNum}: {match.Line}");
        }
    }
}

return anyOutput ? 0 : 1;

// --- Argument parsing ---

static (CliArgs?, int) ParseArgs(string[] args)
{
    if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
    {
        PrintUsage();
        return (null, args.Length == 0 ? 1 : 0);
    }

    string? glob = null;
    string? content = null;
    var excludes = new List<string>();
    bool ignoreCase = false;

    int i = 0;
    while (i < args.Length)
    {
        var arg = args[i];
        switch (arg)
        {
            case "-x" or "--exclude":
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Error: -x requires a pattern argument.");
                    return (null, 1);
                }
                excludes.Add(args[++i]);
                break;
            case "-i" or "--ignore-case":
                ignoreCase = true;
                break;
            default:
                if (arg.StartsWith('-'))
                {
                    Console.Error.WriteLine($"Error: unknown option '{arg}'.");
                    PrintUsage();
                    return (null, 1);
                }
                if (glob is null)
                    glob = arg;
                else if (content is null)
                    content = arg;
                else
                {
                    Console.Error.WriteLine($"Error: unexpected argument '{arg}'.");
                    PrintUsage();
                    return (null, 1);
                }
                break;
        }
        i++;
    }

    if (glob is null)
    {
        Console.Error.WriteLine("Error: glob pattern is required.");
        PrintUsage();
        return (null, 1);
    }

    return (new CliArgs(glob, content, excludes, ignoreCase), 0);
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        Usage: wcg <glob> [pattern] [options]

        Arguments:
          glob       File glob pattern (e.g. "src/**/*.cs")
          pattern    Content search pattern (e.g. "*ERROR*")

        Options:
          -x, --exclude <pattern>   Exclude lines matching pattern (repeatable)
          -i, --ignore-case         Case-insensitive content matching
          -h, --help                Show this help

        Examples:
          wcg "src/**/*.cs"                     List matching files
          wcg "**/*.log" "*ERROR*"               Search for ERROR in log files
          wcg "**/*.cs" "*TODO*" -x "*DONE*"     Search TODO, exclude DONE
          wcg "**/*.cs" "*TODO*" -i              Case-insensitive search
        """);
}

static string? ExtractHighlightLiteral(string pattern, bool ignoreCase)
{
    // Try to extract a literal substring for highlighting via TryMatch on a known match
    // Simple heuristic: strip leading/trailing * to find the core literal
    var trimmed = pattern.AsSpan().Trim('*');
    if (trimmed.Length > 0 && !trimmed.Contains('*') && !trimmed.Contains('?') && !trimmed.Contains('['))
        return trimmed.ToString();
    return null;
}

static void WriteHighlighted(string line, string? literal, bool ignoreCase)
{
    if (literal is not null)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int idx = line.IndexOf(literal, comparison);
        if (idx >= 0)
        {
            Console.Write(line.AsSpan(0, idx));
            Console.Write("\x1b[1;31m");
            Console.Write(line.AsSpan(idx, literal.Length));
            Console.Write("\x1b[0m");
            Console.Write(line.AsSpan(idx + literal.Length));
            return;
        }
    }
    Console.Write(line);
}

record CliArgs(string GlobPattern, string? ContentPattern, List<string> ExcludePatterns, bool IgnoreCase);

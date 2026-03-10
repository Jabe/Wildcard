using System.Text;
using System.Threading.Channels;
using Wildcard;

var (parsed, exitCode) = ParseArgs(args);
if (parsed is null)
    return exitCode;

bool useColor = !Console.IsOutputRedirected;
var cwd = Directory.GetCurrentDirectory();
bool anyOutput = false;

var globOptions = parsed.NoIgnore ? null : new GlobOptions { RespectGitignore = true };

// No content pattern — just list files as they're found
if (parsed.ContentPattern is null)
{
    foreach (var file in Glob.Match(parsed.GlobPattern, options: globOptions))
    {
        Console.WriteLine(Path.GetRelativePath(cwd, file));
        anyOutput = true;
    }
    return anyOutput ? 0 : 1;
}

// Content search — parallel pipeline: glob produces file paths, workers scan in parallel
var matcher = FilePathMatcher.Create(
    include: [parsed.ContentPattern],
    exclude: parsed.ExcludePatterns.Count > 0 ? parsed.ExcludePatterns.ToArray() : null,
    options: parsed.IgnoreCase ? new FilePathMatcher.Options { IgnoreCase = true } : null
);

string? highlightLiteral = ExtractHighlightLiteral(parsed.ContentPattern, parsed.IgnoreCase);
var stdout = Console.Out;

// Producer: glob feeds file paths into a channel
var fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
{
    SingleWriter = true,
    SingleReader = false,
    FullMode = BoundedChannelFullMode.Wait,
});
var producer = Task.Run(async () =>
{
    foreach (var file in Glob.Match(parsed.GlobPattern, options: globOptions))
        await fileChannel.Writer.WriteAsync(file);
    fileChannel.Writer.Complete();
});

// Consumer: parallel workers scan files and write atomic output blocks
var outputLock = new object();
await Parallel.ForEachAsync(fileChannel.Reader.ReadAllAsync(), async (file, _) =>
{
    await Task.CompletedTask; // satisfy async signature
    var fileMatches = matcher.Scan(file);
    if (fileMatches.Count == 0) return;

    var relPath = Path.GetRelativePath(cwd, file);
    int width = fileMatches[^1].LineNumber.ToString().Length;

    var sb = new StringBuilder();
    if (useColor)
        sb.AppendLine($"\x1b[35m{relPath}\x1b[0m");
    else
        sb.AppendLine(relPath);

    foreach (var match in fileMatches)
    {
        var lineNum = match.LineNumber.ToString().PadLeft(width);

        if (useColor)
        {
            sb.Append($"  \x1b[32m{lineNum}\x1b[0m\x1b[36m:\x1b[0m ");
            AppendHighlighted(sb, match.Line, highlightLiteral, parsed.IgnoreCase);
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine($"  {lineNum}: {match.Line}");
        }
    }

    // Atomic write — no interleaving between files
    lock (outputLock)
    {
        if (anyOutput)
            stdout.WriteLine();
        anyOutput = true;
        stdout.Write(sb);
    }
});

await producer;
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
    bool noIgnore = false;

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
            case "--no-ignore":
                noIgnore = true;
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

    return (new CliArgs(glob, content, excludes, ignoreCase, noIgnore), 0);
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
          --no-ignore               Don't respect .gitignore files
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

static void AppendHighlighted(StringBuilder sb, string line, string? literal, bool ignoreCase)
{
    if (literal is not null)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int idx = line.IndexOf(literal, comparison);
        if (idx >= 0)
        {
            sb.Append(line.AsSpan(0, idx));
            sb.Append("\x1b[1;31m");
            sb.Append(line.AsSpan(idx, literal.Length));
            sb.Append("\x1b[0m");
            sb.Append(line.AsSpan(idx + literal.Length));
            return;
        }
    }
    sb.Append(line);
}

record CliArgs(string GlobPattern, string? ContentPattern, List<string> ExcludePatterns, bool IgnoreCase, bool NoIgnore);

using System.Text;
using System.Threading.Channels;
using Wildcard;

var (parsed, exitCode) = ParseArgs(args);
if (parsed is null)
    return exitCode;

bool useColor = !Console.IsOutputRedirected;
var cwd = Directory.GetCurrentDirectory();
bool anyOutput = false;
var stdout = Console.Out;

var globOptions = parsed.NoIgnore ? null : new GlobOptions { RespectGitignore = true };
var excludePathPatterns = parsed.ExcludePathPatterns.Count > 0
    ? parsed.ExcludePathPatterns.Select(p => WildcardPattern.Compile(p)).ToArray()
    : null;

// No content pattern — just list files as they're found
if (parsed.ContentPattern is null)
{
    var knownFiles = parsed.Watch ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
    foreach (var file in Glob.Match(parsed.GlobPattern, options: globOptions))
    {
        var relPath = Path.GetRelativePath(cwd, file).Replace('\\', '/');
        if (IsPathExcluded(relPath, excludePathPatterns)) continue;
        Console.WriteLine(relPath);
        knownFiles?.Add(file);
        anyOutput = true;
    }

    if (!parsed.Watch)
        return anyOutput ? 0 : 1;

    // Watch mode — file list
    await RunWatchLoop(parsed, cwd, useColor, excludePathPatterns, (file) =>
    {
        if (knownFiles!.Add(file))
        {
            stdout.WriteLine(Path.GetRelativePath(cwd, file));
        }
    });
    return 0;
}

// Content search — parallel pipeline: glob produces file paths, workers scan in parallel
var matcher = FilePathMatcher.Create(
    include: [parsed.ContentPattern],
    exclude: parsed.ExcludePatterns.Count > 0 ? parsed.ExcludePatterns.ToArray() : null,
    options: parsed.IgnoreCase ? new FilePathMatcher.Options { IgnoreCase = true } : null
);

string? highlightLiteral = ExtractHighlightLiteral(parsed.ContentPattern, parsed.IgnoreCase);

// Track known match counts per file for watch mode (multiset to handle duplicate line text)
var knownMatches = parsed.Watch
    ? new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
    : null;

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
    {
        var relPath = Path.GetRelativePath(cwd, file).Replace('\\', '/');
        if (IsPathExcluded(relPath, excludePathPatterns)) continue;
        await fileChannel.Writer.WriteAsync(file);
    }
    fileChannel.Writer.Complete();
});

// Consumer: parallel workers scan files and write atomic output blocks
var outputLock = new object();
await Parallel.ForEachAsync(fileChannel.Reader.ReadAllAsync(), async (file, _) =>
{
    await Task.CompletedTask; // satisfy async signature
    var fileMatches = matcher.Scan(file);

    // Track matched line counts for watch mode (multiset for duplicate text)
    if (parsed.Watch)
    {
        var counts = new Dictionary<string, int>(fileMatches.Count);
        foreach (var m in fileMatches)
        {
            counts.TryGetValue(m.Line, out int c);
            counts[m.Line] = c + 1;
        }
        lock (knownMatches!)
            knownMatches[file] = counts;
    }

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

if (!parsed.Watch)
    return anyOutput ? 0 : 1;

// Watch mode — content search: rescan file on change, show only new matches
await RunWatchLoop(parsed, cwd, useColor, excludePathPatterns, (file) =>
{
    try
    {
        if (!File.Exists(file)) return;

        // Rescan the entire file
        var fileMatches = matcher.Scan(file);
        var currentCounts = new Dictionary<string, int>(fileMatches.Count);
        foreach (var m in fileMatches)
        {
            currentCounts.TryGetValue(m.Line, out int c);
            currentCounts[m.Line] = c + 1;
        }

        // Determine which matches are new (multiset diff)
        Dictionary<string, int>? previousCounts;
        lock (knownMatches!)
            knownMatches.TryGetValue(file, out previousCounts);

        List<FilePathMatcher.LineMatch> freshMatches;
        if (previousCounts is not null)
        {
            // Skip oldCount occurrences of each text, emit the rest
            var skipRemaining = new Dictionary<string, int>(previousCounts);
            freshMatches = [];
            foreach (var m in fileMatches)
            {
                if (skipRemaining.TryGetValue(m.Line, out int skip) && skip > 0)
                {
                    skipRemaining[m.Line] = skip - 1;
                    continue;
                }
                freshMatches.Add(m);
            }
        }
        else
        {
            freshMatches = fileMatches;
        }

        // Update known matches
        lock (knownMatches!)
            knownMatches[file] = currentCounts;

        if (freshMatches.Count == 0) return;

        var relPath = Path.GetRelativePath(cwd, file);
        int width = freshMatches[^1].LineNumber.ToString().Length;
        var sb = new StringBuilder();

        if (useColor)
        {
            sb.Append($"\x1b[90m[{DateTime.Now:HH:mm:ss}]\x1b[0m ");
            sb.AppendLine($"\x1b[35m{relPath}\x1b[0m");
        }
        else
        {
            sb.Append($"[{DateTime.Now:HH:mm:ss}] ");
            sb.AppendLine(relPath);
        }

        foreach (var match in freshMatches)
        {
            var lnStr = match.LineNumber.ToString().PadLeft(width);
            if (useColor)
            {
                sb.Append($"  \x1b[32m{lnStr}\x1b[0m\x1b[36m:\x1b[0m ");
                AppendHighlighted(sb, match.Line, highlightLiteral, parsed.IgnoreCase);
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"  {lnStr}: {match.Line}");
            }
        }

        stdout.WriteLine();
        stdout.Write(sb);
    }
    catch { /* file may have been deleted or locked */ }
});
return 0;

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
    var pathExcludes = new List<string>();
    bool ignoreCase = false;
    bool noIgnore = false;
    bool watch = false;

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
            case "-X" or "--exclude-path":
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Error: -X requires a glob pattern argument.");
                    return (null, 1);
                }
                pathExcludes.Add(args[++i]);
                break;
            case "-i" or "--ignore-case":
                ignoreCase = true;
                break;
            case "--no-ignore":
                noIgnore = true;
                break;
            case "-w" or "--watch":
                watch = true;
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

    return (new CliArgs(glob, content, excludes, pathExcludes, ignoreCase, noIgnore, watch), 0);
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        Usage: wcg <glob> [pattern] [options]

        Arguments:
          glob       File glob pattern (e.g. "src/**/*.cs")
          pattern    Content search pattern (e.g. "*ERROR*")

        Options:
          -x, --exclude <pattern>      Exclude lines matching pattern (repeatable)
          -X, --exclude-path <glob>    Exclude files matching glob (repeatable)
          -i, --ignore-case            Case-insensitive content matching
          -w, --watch                  Watch for changes after initial scan
          --no-ignore                  Don't respect .gitignore files
          -h, --help                   Show this help

        Examples:
          wcg "src/**/*.cs"                        List matching files
          wcg "**/*.log" "*ERROR*"                  Search for ERROR in log files
          wcg "**/*.cs" "*TODO*" -x "*DONE*"        Search TODO, exclude DONE
          wcg "**/*.cs" "*TODO*" -i                 Case-insensitive search
          wcg "**/*.log" "*ERROR*" --watch          Watch for new ERROR lines
          wcg "**/*" "*class*" -X "*test*"          Search, skip test paths
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

static bool IsPathExcluded(string relPath, WildcardPattern[]? excludePathPatterns)
{
    if (excludePathPatterns is null) return false;
    foreach (var pattern in excludePathPatterns)
        if (pattern.IsMatch(relPath)) return true;
    return false;
}

static async Task RunWatchLoop(CliArgs parsed, string cwd, bool useColor, WildcardPattern[]? excludePathPatterns, Action<string> onFile)
{
    var baseDir = GlobHelper.GetWatchBaseDirectory(parsed.GlobPattern, cwd);
    bool recursive = GlobHelper.NeedsRecursiveWatch(parsed.GlobPattern);

    // Load gitignore filter for watch mode (unless --no-ignore)
    GitignoreFilter? gitFilter = null;
    string? gitRoot = null;
    if (!parsed.NoIgnore)
    {
        gitRoot = GitignoreFilter.FindGitRoot(baseDir);
        if (gitRoot is not null)
            gitFilter = GitignoreFilter.LoadFromGitRoot(gitRoot);
    }

    if (!Directory.Exists(baseDir))
    {
        Console.Error.WriteLine($"Watch directory does not exist: {baseDir}");
        return;
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    using var watcher = new FileSystemWatcher(baseDir)
    {
        IncludeSubdirectories = recursive,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        EnableRaisingEvents = true,
    };

    var eventChannel = Channel.CreateUnbounded<string>();

    void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (e.FullPath is not null)
            eventChannel.Writer.TryWrite(e.FullPath);
    }
    void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (e.FullPath is not null)
            eventChannel.Writer.TryWrite(e.FullPath);
    }

    watcher.Created += OnEvent;
    watcher.Changed += OnEvent;
    watcher.Renamed += OnRenamed;

    if (useColor)
        Console.Error.WriteLine("\x1b[90m[watching for changes...]\x1b[0m");
    else
        Console.Error.WriteLine("[watching for changes...]");

    try
    {
        while (!cts.Token.IsCancellationRequested)
        {
            var path = await eventChannel.Reader.ReadAsync(cts.Token);
            await Task.Delay(150, cts.Token);

            var batch = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
            while (eventChannel.Reader.TryRead(out var more))
                batch.Add(more);

            foreach (var file in batch)
            {
                if (!File.Exists(file)) continue;
                var matchPath = Path.IsPathRooted(parsed.GlobPattern)
                    ? file.Replace('\\', '/')
                    : Path.GetRelativePath(cwd, file).Replace('\\', '/');
                if (!Glob.IsMatch(parsed.GlobPattern, matchPath)) continue;
                if (IsPathExcluded(matchPath, excludePathPatterns)) continue;
                if (gitFilter is not null && gitRoot is not null)
                {
                    var relToGit = Path.GetRelativePath(gitRoot, file).Replace('\\', '/');
                    if (gitFilter.IsIgnored(relToGit, isDirectory: false))
                        continue;
                }
                onFile(file);
            }
        }
    }
    catch (OperationCanceledException) { }

    Console.Error.WriteLine();
}

record CliArgs(string GlobPattern, string? ContentPattern, List<string> ExcludePatterns, List<string> ExcludePathPatterns, bool IgnoreCase, bool NoIgnore, bool Watch);

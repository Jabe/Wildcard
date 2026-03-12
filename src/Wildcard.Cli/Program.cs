using System.CommandLine;
using System.Text;
using System.Threading.Channels;
using Wildcard;

var globArg = new Argument<string>("glob") { Description = "File glob pattern (e.g. \"src/**/*.cs\")" };
var patternArg = new Argument<string[]>("pattern") { Description = "Content search pattern(s) — multiple patterns are OR'd (e.g. ERROR WARN). Plain words match as substrings; use wildcards for prefix/suffix/full patterns (e.g. \"ERROR*\", \"*.log\").", Arity = ArgumentArity.ZeroOrMore };

var excludeOption = new Option<string[]>("-x", "--exclude") { Description = "Exclude lines matching pattern (repeatable)", AllowMultipleArgumentsPerToken = true };
var excludePathOption = new Option<string[]>("-X", "--exclude-path") { Description = "Exclude files matching glob (repeatable)", AllowMultipleArgumentsPerToken = true };
var ignoreCaseOption = new Option<bool>("-i", "--ignore-case") { Description = "Case-insensitive content matching" };
var filesOnlyOption = new Option<bool>("-l", "--files-with-matches") { Description = "Only print file paths that contain matches" };
var noIgnoreOption = new Option<bool>("--no-ignore") { Description = "Don't respect .gitignore files" };
var followSymlinksOption = new Option<bool>("-L", "--follow") { Description = "Follow symbolic links" };
var watchOption = new Option<bool>("-w", "--watch") { Description = "Watch for changes after initial scan" };
var afterContextOption = new Option<int>("-A", "--after-context") { Description = "Show N lines after each match" };
var beforeContextOption = new Option<int>("-B", "--before-context") { Description = "Show N lines before each match" };
var contextOption = new Option<int>("-C", "--context") { Description = "Show N lines before and after each match" };
var replaceOption = new Option<string?>("-r", "--replace") { Description = "Replace matched content with this string (dry-run by default)" };
var writeOption = new Option<bool>("--write") { Description = "Write replacements to files (default: dry-run preview)" };
var countOption = new Option<bool>("-c", "--count") { Description = "Show count of matching lines per file instead of line content" };

var rootCommand = new RootCommand("Fast wildcard grep tool — glob files, search content with wildcard patterns.")
{
    globArg,
    patternArg,
    excludeOption,
    excludePathOption,
    ignoreCaseOption,
    filesOnlyOption,
    noIgnoreOption,
    followSymlinksOption,
    watchOption,
    afterContextOption,
    beforeContextOption,
    contextOption,
    replaceOption,
    writeOption,
    countOption,
};

rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    int ctxC = parseResult.GetValue(contextOption);
    int ctxB = parseResult.GetValue(beforeContextOption);
    int ctxA = parseResult.GetValue(afterContextOption);
    // For replace mode, we need the raw (un-normalized) content patterns as the find string
    var rawPatterns = parseResult.GetValue(patternArg) ?? [];
    var parsed = new CliArgs(
        parseResult.GetValue(globArg)!,
        [.. rawPatterns.Select(NormalizeContentPattern)],
        [.. (parseResult.GetValue(excludeOption) ?? []).Select(NormalizeContentPattern)],
        [.. parseResult.GetValue(excludePathOption) ?? []],
        parseResult.GetValue(ignoreCaseOption),
        parseResult.GetValue(filesOnlyOption),
        parseResult.GetValue(noIgnoreOption),
        parseResult.GetValue(followSymlinksOption),
        parseResult.GetValue(watchOption),
        ctxB > 0 ? ctxB : ctxC,
        ctxA > 0 ? ctxA : ctxC,
        parseResult.GetValue(replaceOption),
        parseResult.GetValue(writeOption),
        parseResult.GetValue(countOption),
        [.. rawPatterns]
    );

    return await RunAsync(parsed);
});

return await rootCommand.Parse(args).InvokeAsync();

// --- Main logic ---

static async Task<int> RunAsync(CliArgs parsed)
{
    bool useColor = !Console.IsOutputRedirected;
    int maxWidth = !Console.IsOutputRedirected ? Console.WindowWidth : 0;
    var cwd = Directory.GetCurrentDirectory();
    bool anyOutput = false;
    var stdout = Console.Out;

    var globOptions = new GlobOptions
    {
        RespectGitignore = !parsed.NoIgnore,
        FollowSymlinks = parsed.FollowSymlinks,
    };
    var excludePathPatterns = parsed.ExcludePathPatterns.Count > 0
        ? parsed.ExcludePathPatterns.Select(p => WildcardPattern.Compile(p)).ToArray()
        : null;

    // Validation
    if (parsed.Count && parsed.ReplaceWith is not null)
    {
        Console.Error.WriteLine("Error: --count cannot be combined with --replace.");
        return 1;
    }
    if (parsed.Count && parsed.Watch)
    {
        Console.Error.WriteLine("Error: --count cannot be combined with --watch.");
        return 1;
    }

    // Replace mode
    if (parsed.ReplaceWith is not null)
    {
        if (parsed.RawPatterns.Count == 0)
        {
            Console.Error.WriteLine("Error: --replace requires a content pattern to find.");
            return 1;
        }
        if (parsed.Watch)
        {
            Console.Error.WriteLine("Error: --replace cannot be combined with --watch.");
            return 1;
        }

        // Use the first raw pattern as the find string
        var findStr = parsed.RawPatterns[0];
        var replaceStr = parsed.ReplaceWith;

        // Use FilePathMatcher to find files with matches (fast filter)
        var replaceMatcher = FilePathMatcher.Create(
            include: parsed.ContentPatterns.ToArray(),
            exclude: parsed.ExcludePatterns.Count > 0 ? parsed.ExcludePatterns.ToArray() : null,
            options: parsed.IgnoreCase ? new FilePathMatcher.Options { IgnoreCase = true } : null
        );

        var matchingFiles = new List<string>();
        foreach (var file in Glob.Match(parsed.GlobPattern, options: globOptions))
        {
            var relPath = Path.GetRelativePath(cwd, file).Replace('\\', '/');
            if (IsPathExcluded(relPath, excludePathPatterns)) continue;
            if (replaceMatcher.ContainsMatch(file))
                matchingFiles.Add(file);
        }

        if (matchingFiles.Count == 0)
        {
            Console.Error.WriteLine("No matching files found.");
            return 1;
        }

        var results = parsed.WriteChanges
            ? FileReplacer.Apply(matchingFiles.ToArray(), findStr, replaceStr, parsed.IgnoreCase)
            : FileReplacer.Preview(matchingFiles.ToArray(), findStr, replaceStr, parsed.IgnoreCase);

        int totalReplacements = 0;
        int filesChanged = 0;
        int errors = 0;

        foreach (var result in results)
        {
            if (result.Error is not null)
            {
                errors++;
                var relPath = Path.GetRelativePath(cwd, result.FilePath);
                if (useColor)
                    Console.Error.WriteLine($"\x1b[31m{relPath}: {result.Error}\x1b[0m");
                else
                    Console.Error.WriteLine($"{relPath}: {result.Error}");
                continue;
            }

            if (result.Replacements.Count == 0) continue;
            filesChanged++;
            totalReplacements += result.Replacements.Count;

            var relPath2 = Path.GetRelativePath(cwd, result.FilePath);
            int width = result.Replacements[^1].LineNumber.ToString().Length;

            if (useColor)
                stdout.WriteLine($"\x1b[35m{relPath2}\x1b[0m ({result.Replacements.Count} replacement{(result.Replacements.Count > 1 ? "s" : "")})");
            else
                stdout.WriteLine($"{relPath2} ({result.Replacements.Count} replacement{(result.Replacements.Count > 1 ? "s" : "")})");

            foreach (var r in result.Replacements)
            {
                var lineNum = r.LineNumber.ToString().PadLeft(width);
                if (useColor)
                {
                    stdout.WriteLine($"  \x1b[32m{lineNum}\x1b[0m\x1b[31m- {r.OriginalLine}\x1b[0m");
                    stdout.WriteLine($"  \x1b[32m{lineNum}\x1b[0m\x1b[32m+ {r.ReplacedLine}\x1b[0m");
                }
                else
                {
                    stdout.WriteLine($"  {lineNum}: - {r.OriginalLine}");
                    stdout.WriteLine($"  {lineNum}: + {r.ReplacedLine}");
                }
            }
            stdout.WriteLine();
        }

        if (totalReplacements == 0 && errors == 0)
        {
            Console.Error.WriteLine("No replacements found.");
            return 1;
        }

        var summary = parsed.WriteChanges
            ? $"{totalReplacements} replacement{(totalReplacements > 1 ? "s" : "")} applied in {filesChanged} file{(filesChanged > 1 ? "s" : "")}."
            : $"{totalReplacements} replacement{(totalReplacements > 1 ? "s" : "")} in {filesChanged} file{(filesChanged > 1 ? "s" : "")} (dry-run, use --write to apply)";

        if (useColor)
            stdout.WriteLine($"\x1b[1m{summary}\x1b[0m");
        else
            stdout.WriteLine(summary);

        if (errors > 0)
        {
            var errMsg = $"{errors} file{(errors > 1 ? "s" : "")} failed (see errors above).";
            if (useColor)
                Console.Error.WriteLine($"\x1b[31m{errMsg}\x1b[0m");
            else
                Console.Error.WriteLine(errMsg);
        }

        return errors > 0 ? 2 : 0;
    }

    // No content pattern — just list files as they're found
    if (parsed.ContentPatterns.Count == 0)
    {
        if (parsed.Count)
        {
            int fileCount = 0;
            foreach (var file in Glob.Match(parsed.GlobPattern, options: globOptions))
            {
                var relPath = Path.GetRelativePath(cwd, file).Replace('\\', '/');
                if (IsPathExcluded(relPath, excludePathPatterns)) continue;
                fileCount++;
            }
            if (fileCount == 0)
            {
                Console.Error.WriteLine("No files found.");
                return 1;
            }
            var msg = $"{fileCount} file{(fileCount > 1 ? "s" : "")} found.";
            stdout.WriteLine(useColor ? $"\x1b[1m{msg}\x1b[0m" : msg);
            return 0;
        }

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
        include: parsed.ContentPatterns.ToArray(),
        exclude: parsed.ExcludePatterns.Count > 0 ? parsed.ExcludePatterns.ToArray() : null,
        options: parsed.IgnoreCase ? new FilePathMatcher.Options { IgnoreCase = true } : null
    );

    // Count mode: per-file match counts + summary
    if (parsed.Count)
    {
        var countChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            SingleWriter = false, SingleReader = false, FullMode = BoundedChannelFullMode.Wait,
        });
        var countProducer = Task.Run(() =>
        {
            try
            {
                var glob = Glob.Parse(parsed.GlobPattern);
                glob.WriteMatchesToChannel(countChannel.Writer, globOptions);
            }
            finally { countChannel.Writer.Complete(); }
        });

        int totalMatches = 0;
        int totalFiles = 0;
        var countLock = new object();

        await Parallel.ForEachAsync(countChannel.Reader.ReadAllAsync(), async (file, _) =>
        {
            await Task.CompletedTask;
            var relPath = Path.GetRelativePath(cwd, file).Replace('\\', '/');
            if (IsPathExcluded(relPath, excludePathPatterns)) return;
            var fileMatches = matcher.Scan(file);
            if (fileMatches.Count == 0) return;

            lock (countLock)
            {
                totalMatches += fileMatches.Count;
                totalFiles++;
                if (!parsed.FilesOnly)
                {
                    if (useColor)
                        stdout.WriteLine($"\x1b[35m{relPath}\x1b[0m:\x1b[32m{fileMatches.Count}\x1b[0m");
                    else
                        stdout.WriteLine($"{relPath}:{fileMatches.Count}");
                }
            }
        });
        await countProducer;

        if (totalMatches == 0)
        {
            Console.Error.WriteLine("No matches found.");
            return 1;
        }

        if (!parsed.FilesOnly)
            stdout.WriteLine();
        var summary = $"{totalMatches} match{(totalMatches > 1 ? "es" : "")} in {totalFiles} file{(totalFiles > 1 ? "s" : "")}.";
        stdout.WriteLine(useColor ? $"\x1b[1m{summary}\x1b[0m" : summary);
        return 0;
    }

    // Files-only mode: just print file paths that contain matches
    if (parsed.FilesOnly)
    {
        var filesOnlyChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            SingleWriter = false, SingleReader = false, FullMode = BoundedChannelFullMode.Wait,
        });
        var filesOnlyProducer = Task.Run(() =>
        {
            try
            {
                var glob = Glob.Parse(parsed.GlobPattern);
                glob.WriteMatchesToChannel(filesOnlyChannel.Writer, globOptions);
            }
            finally { filesOnlyChannel.Writer.Complete(); }
        });
        var filesOnlyLock = new object();
        await Parallel.ForEachAsync(filesOnlyChannel.Reader.ReadAllAsync(), async (file, _) =>
        {
            await Task.CompletedTask;
            var relPath = Path.GetRelativePath(cwd, file).Replace('\\', '/');
            if (IsPathExcluded(relPath, excludePathPatterns)) return;
            if (matcher.ContainsMatch(file))
            {
                lock (filesOnlyLock)
                {
                    stdout.WriteLine(Path.GetRelativePath(cwd, file));
                    anyOutput = true;
                }
            }
        });
        await filesOnlyProducer;
        return anyOutput ? 0 : 1;
    }

    string[] highlightLiterals = parsed.ContentPatterns
        .Select(p => ExtractHighlightLiteral(p, parsed.IgnoreCase))
        .Where(l => l is not null)
        .Select(l => l!)
        .ToArray();

    // Track known match counts per file for watch mode (multiset to handle duplicate line text)
    var knownMatches = parsed.Watch
        ? new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
        : null;

    // Producer: parallel glob feeds file paths into a channel
    var fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
    {
        SingleWriter = false,
        SingleReader = false,
        FullMode = BoundedChannelFullMode.Wait,
    });
    var producer = Task.Run(() =>
    {
        try
        {
            if (excludePathPatterns is not null)
            {
                // With path exclusions: use sequential glob to filter before writing
                foreach (var file in Glob.Match(parsed.GlobPattern, options: globOptions))
                {
                    var relPath = Path.GetRelativePath(cwd, file).Replace('\\', '/');
                    if (IsPathExcluded(relPath, excludePathPatterns)) continue;
                    Glob.WriteBlocking(fileChannel.Writer, file);
                }
            }
            else
            {
                // No path exclusions: use parallel glob walker
                var glob = Glob.Parse(parsed.GlobPattern);
                glob.WriteMatchesToChannel(fileChannel.Writer, globOptions);
            }
        }
        finally { fileChannel.Writer.Complete(); }
    });

    // Consumer: parallel workers scan files and write atomic output blocks
    bool hasContext = parsed.BeforeContext > 0 || parsed.AfterContext > 0;
    var outputLock = new object();
    await Parallel.ForEachAsync(fileChannel.Reader.ReadAllAsync(), async (file, _) =>
    {
        await Task.CompletedTask; // satisfy async signature

        if (hasContext)
        {
            var contextLines = matcher.ScanWithContext(parsed.BeforeContext, parsed.AfterContext, file);
            if (contextLines.Count == 0) return;

            // Track matched line counts for watch mode
            if (parsed.Watch)
            {
                var counts = new Dictionary<string, int>();
                foreach (var cl in contextLines)
                {
                    if (!cl.IsMatch) continue;
                    counts.TryGetValue(cl.Line, out int c);
                    counts[cl.Line] = c + 1;
                }
                lock (knownMatches!)
                    knownMatches[file] = counts;
            }

            var relPath = Path.GetRelativePath(cwd, file);
            int width = contextLines[^1].LineNumber.ToString().Length;

            var sb = new StringBuilder();
            if (useColor)
                sb.AppendLine($"\x1b[35m{relPath}\x1b[0m");
            else
                sb.AppendLine(relPath);

            int availableWidth = maxWidth > 0 ? maxWidth - (2 + width + 2) : 0;
            for (int i = 0; i < contextLines.Count; i++)
            {
                // Insert group separator for non-contiguous lines
                if (i > 0 && contextLines[i].LineNumber != contextLines[i - 1].LineNumber + 1)
                {
                    if (useColor)
                        sb.AppendLine($"  \x1b[36m{"--".PadLeft(width)}\x1b[0m");
                    else
                        sb.AppendLine($"  {"--".PadLeft(width)}");
                }

                var cl = contextLines[i];
                var lineNum = cl.LineNumber.ToString().PadLeft(width);

                if (cl.IsMatch)
                {
                    var lineText = TruncateLine(cl.Line, availableWidth, highlightLiterals, parsed.IgnoreCase);
                    if (useColor)
                    {
                        sb.Append($"  \x1b[32m{lineNum}\x1b[0m\x1b[36m:\x1b[0m ");
                        AppendHighlighted(sb, lineText, highlightLiterals, parsed.IgnoreCase);
                        sb.AppendLine();
                    }
                    else
                    {
                        sb.AppendLine($"  {lineNum}: {lineText}");
                    }
                }
                else
                {
                    var lineText = TruncateLine(cl.Line, availableWidth, highlightLiterals, parsed.IgnoreCase);
                    if (useColor)
                        sb.AppendLine($"  \x1b[32m{lineNum}\x1b[0m\x1b[36m-\x1b[0m {lineText}");
                    else
                        sb.AppendLine($"  {lineNum}- {lineText}");
                }
            }

            lock (outputLock)
            {
                if (anyOutput)
                    stdout.WriteLine();
                anyOutput = true;
                stdout.Write(sb);
            }
        }
        else
        {
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

            int availableWidth = maxWidth > 0 ? maxWidth - (2 + width + 2) : 0;
            foreach (var match in fileMatches)
            {
                var lineNum = match.LineNumber.ToString().PadLeft(width);
                var lineText = TruncateLine(match.Line, availableWidth, highlightLiterals, parsed.IgnoreCase);

                if (useColor)
                {
                    sb.Append($"  \x1b[32m{lineNum}\x1b[0m\x1b[36m:\x1b[0m ");
                    AppendHighlighted(sb, lineText, highlightLiterals, parsed.IgnoreCase);
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine($"  {lineNum}: {lineText}");
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

            int availableWidth = maxWidth > 0 ? maxWidth - (2 + width + 2) : 0;
            foreach (var match in freshMatches)
            {
                var lnStr = match.LineNumber.ToString().PadLeft(width);
                var lineText = TruncateLine(match.Line, availableWidth, highlightLiterals, parsed.IgnoreCase);
                if (useColor)
                {
                    sb.Append($"  \x1b[32m{lnStr}\x1b[0m\x1b[36m:\x1b[0m ");
                    AppendHighlighted(sb, lineText, highlightLiterals, parsed.IgnoreCase);
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine($"  {lnStr}: {lineText}");
                }
            }

            stdout.WriteLine();
            stdout.Write(sb);
        }
        catch { /* file may have been deleted or locked */ }
    });
    return 0;
}

// --- Helpers ---

static string NormalizeContentPattern(string pattern)
{
    // Plain strings (no unescaped wildcard chars) are treated as substring matches: wrap as *<pattern>*
    for (int i = 0; i < pattern.Length; i++)
    {
        if (pattern[i] == '\\') { i++; continue; } // skip escaped char
        if (pattern[i] is '*' or '?' or '[') return pattern;
    }
    return $"*{pattern}*";
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

static void AppendHighlighted(StringBuilder sb, string line, string[] literals, bool ignoreCase)
{
    if (literals.Length == 0) { sb.Append(line); return; }
    var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Collect all (start, length) highlight spans across all literals, then render in order.
    var spans = new List<(int Start, int Length)>();
    foreach (var literal in literals)
    {
        int pos = 0;
        while (pos < line.Length)
        {
            int idx = line.IndexOf(literal, pos, comparison);
            if (idx < 0) break;
            spans.Add((idx, literal.Length));
            pos = idx + literal.Length;
        }
    }

    if (spans.Count == 0) { sb.Append(line); return; }

    spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));

    int cursor = 0;
    foreach (var (start, length) in spans)
    {
        if (start < cursor) continue; // overlapped by a previous span
        sb.Append(line.AsSpan(cursor, start - cursor));
        sb.Append("\x1b[1;31m");
        sb.Append(line.AsSpan(start, length));
        sb.Append("\x1b[0m");
        cursor = start + length;
    }
    sb.Append(line.AsSpan(cursor));
}

static string TruncateLine(string line, int availableWidth, string[] highlightLiterals, bool ignoreCase)
{
    if (availableWidth <= 0 || line.Length <= availableWidth)
        return line;

    // Find all match positions across all literals
    var matchIndices = new List<int>();
    int matchLen = 0;
    if (highlightLiterals.Length > 0)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var highlightLiteral in highlightLiterals)
        {
            if (highlightLiteral.Length == 0) continue;
            int pos = 0;
            while (pos <= line.Length - highlightLiteral.Length)
            {
                int idx = line.IndexOf(highlightLiteral, pos, comparison);
                if (idx < 0) break;
                matchIndices.Add(idx);
                pos = idx + highlightLiteral.Length;
            }
            // Use longest literal length as the representative match length for snippet sizing
            if (highlightLiteral.Length > matchLen) matchLen = highlightLiteral.Length;
        }
        matchIndices.Sort();
    }

    // No matches — truncate from the right
    if (matchIndices.Count == 0)
        return string.Concat(line.AsSpan(0, availableWidth - 1), "…");

    // Determine how many matches we can show (min 20 chars per snippet)
    int showCount = matchIndices.Count;
    while (showCount > 1)
    {
        int ellipses = showCount + 1; // leading + between + trailing (worst case)
        int perMatch = (availableWidth - ellipses) / showCount;
        if (perMatch >= 20) break;
        showCount--;
    }

    // Build snippet spans centered on each match, then merge overlapping ones
    int totalEllipses = showCount + 1;
    int perSnippet = (availableWidth - totalEllipses) / showCount;

    // Calculate raw spans
    var spans = new List<(int Start, int End)>(showCount);
    for (int i = 0; i < showCount; i++)
    {
        int center = matchIndices[i] + matchLen / 2;
        int start = center - perSnippet / 2;
        int end = start + perSnippet;
        spans.Add((start, end));
    }

    // Merge overlapping or adjacent spans
    var merged = new List<(int Start, int End)> { spans[0] };
    for (int i = 1; i < spans.Count; i++)
    {
        var last = merged[^1];
        if (spans[i].Start <= last.End + 1)
            merged[^1] = (last.Start, Math.Max(last.End, spans[i].End));
        else
            merged.Add(spans[i]);
    }

    // Redistribute width after merging (fewer ellipsis separators needed)
    if (merged.Count < showCount)
    {
        int newEllipses = merged.Count + 1;
        int extraChars = (totalEllipses - newEllipses);
        // Spread extra chars across merged spans
        int perExtra = extraChars / merged.Count;
        for (int i = 0; i < merged.Count; i++)
        {
            var (s, e) = merged[i];
            merged[i] = (s - perExtra / 2, e + (perExtra - perExtra / 2));
        }
    }

    // Clamp all spans to line bounds
    for (int i = 0; i < merged.Count; i++)
    {
        var (s, e) = merged[i];
        if (s < 0) { e -= s; s = 0; }
        if (e > line.Length) { s -= (e - line.Length); e = line.Length; s = Math.Max(0, s); }
        merged[i] = (s, e);
    }

    // Build output
    var sb = new StringBuilder(availableWidth + 4);
    for (int i = 0; i < merged.Count; i++)
    {
        var (s, e) = merged[i];
        bool needLeading = (i == 0 && s > 0) || i > 0;
        bool needTrailing = (i == merged.Count - 1 && e < line.Length) || i < merged.Count - 1;

        if (needLeading) sb.Append('…');
        sb.Append(line.AsSpan(s, e - s));
        if (needTrailing && i == merged.Count - 1) sb.Append('…');
    }

    return sb.ToString();
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

record CliArgs(string GlobPattern, List<string> ContentPatterns, List<string> ExcludePatterns, List<string> ExcludePathPatterns, bool IgnoreCase, bool FilesOnly, bool NoIgnore, bool FollowSymlinks, bool Watch, int BeforeContext, int AfterContext, string? ReplaceWith, bool WriteChanges, bool Count, List<string> RawPatterns);

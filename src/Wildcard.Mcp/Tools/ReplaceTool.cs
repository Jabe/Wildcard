using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace Wildcard.Mcp.Tools;

[McpServerToolType]
public static class ReplaceTool
{
    [McpServerTool(Name = "wildcard_replace"), Description("sed wishes it could. Find-and-replace across files with glob patterns — dry-run preview by default, atomic writes, capture groups ($1/$2). Way safer than regex-in-bash. Set dry_run=false to actually write.")]
    public static async Task<string> Replace(
        [Description("File glob pattern (e.g. \"**/*.cs\", \"src/**/*.ts\", \"**/*.{cs,razor,css}\")")] string pattern,
        [Description("Text to find — plain string for literal match, or wildcard pattern with * ? [] for capture-group replacement")] string find,
        [Description("Replacement text (use $1, $2 for capture groups when find contains wildcards)")] string replace,
        [Description("Base directory to search in (defaults to current working directory)")] string? base_directory = null,
        [Description("Exclude files matching these glob patterns")] string[]? exclude_paths = null,
        [Description("Case-insensitive matching (default: false)")] bool ignore_case = false,
        [Description("Honor .gitignore files (default: true)")] bool respect_gitignore = true,
        [Description("Follow symbolic links (default: false)")] bool follow_symlinks = false,
        [Description("Preview only, don't write changes (default: true)")] bool dry_run = true,
        [Description("Maximum number of files to process (default: 50)")] int limit = 50,
        WorkspaceIndex? index = null,
        CancellationToken cancellationToken = default)
    {
        var (baseDir, guardError) = PathGuard.Resolve(base_directory);
        if (guardError is not null) return guardError;
        var globOptions = new GlobOptions
        {
            RespectGitignore = respect_gitignore,
            FollowSymlinks = follow_symlinks,
        };

        if (string.IsNullOrEmpty(find))
            return "Error: find string cannot be empty.";

        return await Task.Run(() =>
        {
            // Normalize find pattern for file matching (auto-wrap plain words as *word*)
            var normalizedFind = NormalizeContentPattern(find);

            WildcardPattern[]? excludePathPatterns = null;
            if (exclude_paths is { Length: > 0 })
                excludePathPatterns = exclude_paths.Select(p => WildcardPattern.Compile(p)).ToArray();

            var matcher = FilePathMatcher.Create(
                include: [normalizedFind],
                options: ignore_case ? new FilePathMatcher.Options { IgnoreCase = true } : null);

            // Use index when available and options match indexed state
            var activeIndex = index is not null && respect_gitignore && !follow_symlinks ? index : null;

            // Find files with matches
            var matchingFiles = new List<string>();

            if (activeIndex is not null)
            {
                foreach (var file in activeIndex.MatchGlob(pattern, baseDir, exclude_paths))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (matchingFiles.Count >= limit) break;
                    if (matcher.ContainsMatch(file))
                        matchingFiles.Add(file);
                }
            }
            else
            {
                foreach (var file in Wildcard.Glob.Match(pattern, baseDir, globOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (matchingFiles.Count >= limit) break;
                    var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
                    if (IsPathExcluded(relPath, excludePathPatterns)) continue;
                    if (matcher.ContainsMatch(file))
                        matchingFiles.Add(file);
                }
            }

            if (matchingFiles.Count == 0)
                return "No matching files found.";

            // Run replacement
            var results = dry_run
                ? FileReplacer.Preview(matchingFiles.ToArray(), find, replace, ignore_case)
                : FileReplacer.Apply(matchingFiles.ToArray(), find, replace, ignore_case);

            // Proactively update index after writes
            if (!dry_run && index is not null)
            {
                foreach (var result in results)
                {
                    if (result.Error is null && result.Replacements.Count > 0)
                        index.NotifyFileWritten(result.FilePath);
                }
            }

            if (results.Count == 0)
                return "No replacements found.";

            var sb = new StringBuilder();
            int totalReplacements = 0;
            int filesChanged = 0;
            bool anyOutput = false;

            int errors = 0;
            foreach (var result in results)
            {
                if (result.Error is not null)
                {
                    errors++;
                    var relPath = Path.GetRelativePath(baseDir, result.FilePath).Replace('\\', '/');
                    if (anyOutput) sb.AppendLine();
                    anyOutput = true;
                    sb.AppendLine($"{relPath}: ERROR — {result.Error}");
                    continue;
                }

                if (result.Replacements.Count == 0) continue;
                filesChanged++;
                totalReplacements += result.Replacements.Count;

                var relPath2 = Path.GetRelativePath(baseDir, result.FilePath).Replace('\\', '/');
                int width = result.Replacements[^1].LineNumber.ToString().Length;

                if (anyOutput)
                    sb.AppendLine();
                anyOutput = true;

                sb.AppendLine($"{relPath2} ({result.Replacements.Count} replacement{(result.Replacements.Count > 1 ? "s" : "")})");

                foreach (var r in result.Replacements)
                {
                    var lineNum = r.LineNumber.ToString().PadLeft(width);
                    sb.AppendLine($"  {lineNum}: - {r.OriginalLine}");
                    sb.AppendLine($"  {lineNum}: + {r.ReplacedLine}");
                }
            }

            sb.AppendLine();
            if (dry_run)
                sb.AppendLine($"Summary: {totalReplacements} replacement{(totalReplacements > 1 ? "s" : "")} in {filesChanged} file{(filesChanged > 1 ? "s" : "")} (dry-run)");
            else
                sb.AppendLine($"Summary: {totalReplacements} replacement{(totalReplacements > 1 ? "s" : "")} applied in {filesChanged} file{(filesChanged > 1 ? "s" : "")}.");

            if (errors > 0)
                sb.AppendLine($"{errors} file{(errors > 1 ? "s" : "")} failed (see errors above).");

            return sb.ToString();
        }, cancellationToken);
    }

    private static string NormalizeContentPattern(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\') { i++; continue; }
            if (pattern[i] is '*' or '?' or '[') return pattern;
        }
        return $"*{pattern}*";
    }

    private static bool IsPathExcluded(string relPath, WildcardPattern[]? excludePathPatterns)
    {
        if (excludePathPatterns is null) return false;
        foreach (var p in excludePathPatterns)
            if (p.IsMatch(relPath)) return true;
        return false;
    }
}

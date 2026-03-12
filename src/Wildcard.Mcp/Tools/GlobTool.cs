using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Wildcard;

namespace Wildcard.Mcp.Tools;

[McpServerToolType]
public static class GlobTool
{
    [McpServerTool(Name = "wildcard_glob"), Description("Find files matching a glob pattern. Ultra-fast, respects .gitignore. Supports ** for recursive matching, * for wildcards, ? for single char, [abc] for character classes.")]
    public static string Glob(
        [Description("Glob pattern (e.g. \"**/*.cs\", \"src/**/*.ts\", \"*.json\")")] string pattern,
        [Description("Base directory to search in (defaults to current working directory)")] string? base_directory = null,
        [Description("Glob patterns to exclude file paths (e.g. \"**/node_modules/**\")")] string[]? exclude_paths = null,
        [Description("Honor .gitignore files (default: true)")] bool respect_gitignore = true,
        [Description("Follow symbolic links (default: false)")] bool follow_symlinks = false,
        [Description("Maximum number of results to return (default: 10000)")] int limit = 10000)
    {
        var baseDir = base_directory ?? Directory.GetCurrentDirectory();
        var options = new GlobOptions
        {
            RespectGitignore = respect_gitignore,
            FollowSymlinks = follow_symlinks,
        };

        WildcardPattern[]? excludePatterns = null;
        if (exclude_paths is { Length: > 0 })
            excludePatterns = exclude_paths.Select(p => WildcardPattern.Compile(p)).ToArray();

        var sb = new StringBuilder();
        int count = 0;
        int total = 0;

        foreach (var file in Wildcard.Glob.Match(pattern, baseDir, options))
        {
            total++;
            var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');

            if (excludePatterns is not null)
            {
                bool excluded = false;
                foreach (var ep in excludePatterns)
                {
                    if (ep.IsMatch(relPath)) { excluded = true; break; }
                }
                if (excluded) continue;
            }

            count++;
            if (count <= limit)
            {
                sb.AppendLine(relPath);
            }
        }

        if (count == 0)
            return "No files found.";

        if (count > limit)
            sb.AppendLine($"\n... and {count - limit} more files ({count} total, showing first {limit})");
        else
            sb.AppendLine($"\n{count} files found.");

        return sb.ToString();
    }
}

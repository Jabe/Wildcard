using System.ComponentModel;
using System.Text;
using System.Threading.Channels;
using ModelContextProtocol.Server;
using Wildcard;

namespace Wildcard.Mcp.Tools;

[McpServerToolType]
public static class WatchTool
{
    [McpServerTool(Name = "wildcard_watch"), Description("fswatch but actually useful. Watch for file changes matching a glob pattern for a bounded duration — returns a summary of creates/modifies/deletes. Great for monitoring builds, tests, or deploys in real time.")]
    public static async Task<string> Watch(
        [Description("Glob pattern for files to watch (e.g. \"**/*.log\", \"src/**/*.cs\", \"**/*.{cs,razor}\")")] string pattern,
        [Description("Base directory to watch in (defaults to current working directory)")] string? base_directory = null,
        [Description("Content patterns to filter changes (optional). Only report changes in files containing these patterns.")] string[]? content_patterns = null,
        [Description("Watch duration in seconds (default: 30, max: 120)")] int duration_seconds = 30,
        [Description("Honor .gitignore files (default: true)")] bool respect_gitignore = true,
        CancellationToken cancellationToken = default)
    {
        var summary = ArgSummary.Create()
            .Arg("pattern", pattern)
            .Arg("base_directory", base_directory)
            .Arg("content_patterns", content_patterns)
            .Arg("duration_seconds", duration_seconds, 30)
            .Arg("respect_gitignore", respect_gitignore, true)
            .ToString();

        var baseDir = base_directory ?? Directory.GetCurrentDirectory();
        duration_seconds = Math.Clamp(duration_seconds, 1, 120);

        var watchBaseDir = GlobHelper.GetWatchBaseDirectory(pattern, baseDir);
        bool recursive = GlobHelper.NeedsRecursiveWatch(pattern);

        if (!Directory.Exists(watchBaseDir))
            return summary + $"Watch directory does not exist: {watchBaseDir}";

        // Load gitignore filter
        GitignoreFilter? gitFilter = null;
        string? gitRoot = null;
        if (respect_gitignore)
        {
            gitRoot = GitignoreFilter.FindGitRoot(watchBaseDir);
            if (gitRoot is not null)
                gitFilter = GitignoreFilter.LoadFromGitRoot(gitRoot);
        }

        // Set up content matcher if patterns provided
        FilePathMatcher? matcher = null;
        if (content_patterns is { Length: > 0 })
        {
            var normalized = content_patterns.Select(NormalizeContentPattern).ToArray();
            matcher = FilePathMatcher.Create(include: normalized);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(duration_seconds));

        using var watcher = new FileSystemWatcher(watchBaseDir)
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

        var changes = new List<ChangeRecord>();

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

                    var matchPath = Path.IsPathRooted(pattern)
                        ? file.Replace('\\', '/')
                        : Path.GetRelativePath(baseDir, file).Replace('\\', '/');

                    if (!Wildcard.Glob.IsMatch(pattern, matchPath)) continue;

                    if (gitFilter is not null && gitRoot is not null)
                    {
                        var relToGit = Path.GetRelativePath(gitRoot, file).Replace('\\', '/');
                        if (gitFilter.IsIgnored(relToGit, isDirectory: false))
                            continue;
                    }

                    if (matcher is not null && !matcher.ContainsMatch(file))
                        continue;

                    var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
                    changes.Add(new ChangeRecord(DateTime.Now, relPath));
                }
            }
        }
        catch (OperationCanceledException) { }

        if (changes.Count == 0)
            return summary + $"No changes detected during {duration_seconds}s watch period.";

        var sb = new StringBuilder();
        sb.AppendLine($"Changes detected during {duration_seconds}s watch period:");
        sb.AppendLine();

        var grouped = changes.GroupBy(c => c.Path).OrderBy(g => g.First().Timestamp);
        foreach (var group in grouped)
        {
            var times = group.Select(c => c.Timestamp.ToString("HH:mm:ss")).Distinct();
            sb.AppendLine($"  {group.Key} ({string.Join(", ", times)})");
        }

        sb.AppendLine();
        sb.AppendLine($"{changes.Count} change events across {grouped.Count()} files.");

        return summary + sb.ToString();
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

    private record ChangeRecord(DateTime Timestamp, string Path);
}

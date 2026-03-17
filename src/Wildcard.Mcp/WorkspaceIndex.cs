using System.Collections.Concurrent;
using System.Threading.Channels;
using Wildcard;

namespace Wildcard.Mcp;

/// <summary>
/// In-memory file-path index kept current by FileSystemWatcher.
/// Enabled via the --live CLI flag. Glob queries become O(filter) instead of O(walk).
/// </summary>
public sealed class WorkspaceIndex : IDisposable
{
    private readonly string _rootDir;
    private readonly ConcurrentDictionary<string, byte> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher _watcher;
    private readonly Channel<FileSystemEventArgs> _eventChannel = Channel.CreateUnbounded<FileSystemEventArgs>();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile GitignoreFilter? _gitFilter;
    private volatile string? _gitRoot;

    public WorkspaceIndex(string rootDir)
    {
        _rootDir = Path.GetFullPath(rootDir);

        _gitRoot = GitignoreFilter.FindGitRoot(_rootDir);
        if (_gitRoot is not null)
            _gitFilter = GitignoreFilter.LoadFromGitRoot(_gitRoot);

        // Start FSW before initial scan to avoid missing creates during scan
        _watcher = new FileSystemWatcher(_rootDir)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
        };
        _watcher.Created += OnEvent;
        _watcher.Deleted += OnEvent;
        _watcher.Changed += OnEvent;
        _watcher.Renamed += OnRenamed;

        // Single background task: scan first, then process queued events
        _ = Task.Run(RunAsync);
    }

    /// <summary>Awaitable signal that initial scan is complete.</summary>
    public Task WaitForReady() => _ready.Task;

    /// <summary>Number of files currently in the index.</summary>
    public int FileCount => _paths.Count;

    /// <summary>
    /// Returns indexed file paths matching the glob pattern, as absolute paths.
    /// Exclude paths are checked against the path relative to <paramref name="baseDir"/>.
    /// </summary>
    public IEnumerable<string> MatchGlob(string pattern, string baseDir, string[]? excludePaths)
    {
        var glob = Glob.Parse(pattern);

        WildcardPattern[]? excludePatterns = null;
        if (excludePaths is { Length: > 0 })
            excludePatterns = excludePaths.Select(p => WildcardPattern.Compile(p)).ToArray();

        var relBase = baseDir.Equals(_rootDir, StringComparison.OrdinalIgnoreCase)
            ? ""
            : Path.GetRelativePath(_rootDir, baseDir).Replace('\\', '/');

        foreach (var relPath in _paths.Keys)
        {
            string pathForMatch;

            if (relBase.Length == 0)
            {
                pathForMatch = relPath;
            }
            else if (relPath.StartsWith(relBase + "/", StringComparison.OrdinalIgnoreCase))
            {
                pathForMatch = relPath.Substring(relBase.Length + 1);
            }
            else
            {
                continue;
            }

            if (!glob.IsMatch(pathForMatch)) continue;

            if (excludePatterns is not null)
            {
                bool excluded = false;
                foreach (var ep in excludePatterns)
                    if (ep.IsMatch(pathForMatch)) { excluded = true; break; }
                if (excluded) continue;
            }

            yield return Path.Combine(baseDir, pathForMatch.Replace('/', Path.DirectorySeparatorChar));
        }
    }

    /// <summary>Proactively add/update a path after a write operation.</summary>
    public void NotifyFileWritten(string absolutePath)
    {
        var relPath = ToRelativePath(absolutePath);
        if (relPath is not null && !IsIgnored(relPath))
            _paths[relPath] = 0;
    }

    /// <summary>Proactively remove a path.</summary>
    public void NotifyFileDeleted(string absolutePath)
    {
        var relPath = ToRelativePath(absolutePath);
        if (relPath is not null)
            _paths.TryRemove(relPath, out _);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnEvent;
        _watcher.Deleted -= OnEvent;
        _watcher.Changed -= OnEvent;
        _watcher.Renamed -= OnRenamed;
        _watcher.Dispose();
        _eventChannel.Writer.TryComplete();
        _cts.Dispose();
    }

    // ── Private ──────────────────────────────────────────────

    private async Task RunAsync()
    {
        try
        {
            var options = new GlobOptions { RespectGitignore = true };
            foreach (var file in Glob.Match("**/*", _rootDir, options))
                _paths.TryAdd(Path.GetRelativePath(_rootDir, file).Replace('\\', '/'), 0);
        }
        finally
        {
            _ready.TrySetResult();
        }

        await ProcessEventsAsync();
    }

    private async Task ProcessEventsAsync()
    {
        var token = _cts.Token;
        try
        {
            while (await _eventChannel.Reader.WaitToReadAsync(token))
            {
                await Task.Delay(150, token);

                var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool gitignoreChanged = false;

                while (_eventChannel.Reader.TryRead(out var evt))
                {
                    if (evt is RenamedEventArgs renamed)
                    {
                        var oldRel = ToRelativePath(renamed.OldFullPath);
                        if (oldRel is not null) deleted.Add(oldRel);
                        created.Add(renamed.FullPath);
                    }
                    else if (evt.ChangeType == WatcherChangeTypes.Deleted)
                    {
                        var rel = ToRelativePath(evt.FullPath);
                        if (rel is not null) deleted.Add(rel);
                    }
                    else
                    {
                        created.Add(evt.FullPath);
                    }

                    if (Path.GetFileName(evt.FullPath).Equals(".gitignore", StringComparison.OrdinalIgnoreCase))
                        gitignoreChanged = true;
                }

                foreach (var rel in deleted)
                    _paths.TryRemove(rel, out _);

                if (gitignoreChanged)
                {
                    ReloadGitignoreAndRebuild();
                    continue;
                }

                foreach (var fullPath in created)
                {
                    var rel = ToRelativePath(fullPath);
                    if (rel is null) continue;

                    if (File.Exists(fullPath) && !IsIgnored(rel))
                        _paths[rel] = 0;
                    else
                        _paths.TryRemove(rel, out _);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnEvent(object sender, FileSystemEventArgs e) =>
        _eventChannel.Writer.TryWrite(e);

    private void OnRenamed(object sender, RenamedEventArgs e) =>
        _eventChannel.Writer.TryWrite(e);

    private string? ToRelativePath(string absolutePath)
    {
        if (!absolutePath.StartsWith(_rootDir, StringComparison.Ordinal)) return null;
        if (absolutePath.Length > _rootDir.Length && absolutePath[_rootDir.Length] != Path.DirectorySeparatorChar)
            return null;
        return Path.GetRelativePath(_rootDir, absolutePath).Replace('\\', '/');
    }

    private bool IsIgnored(string relPathFromRoot)
    {
        var filter = _gitFilter;
        var gitRoot = _gitRoot;
        if (filter is null || gitRoot is null) return false;

        string relToGit;
        if (_rootDir.Equals(gitRoot, StringComparison.OrdinalIgnoreCase))
        {
            relToGit = relPathFromRoot;
        }
        else
        {
            var absPath = Path.Combine(_rootDir, relPathFromRoot.Replace('/', Path.DirectorySeparatorChar));
            relToGit = Path.GetRelativePath(gitRoot, absPath).Replace('\\', '/');
        }

        return filter.IsIgnored(relToGit, isDirectory: false);
    }

    private void ReloadGitignoreAndRebuild()
    {
        _gitRoot = GitignoreFilter.FindGitRoot(_rootDir);
        _gitFilter = _gitRoot is not null ? GitignoreFilter.LoadFromGitRoot(_gitRoot) : null;

        _paths.Clear();
        var options = new GlobOptions { RespectGitignore = true };
        foreach (var file in Glob.Match("**/*", _rootDir, options))
            _paths.TryAdd(Path.GetRelativePath(_rootDir, file).Replace('\\', '/'), 0);
    }
}

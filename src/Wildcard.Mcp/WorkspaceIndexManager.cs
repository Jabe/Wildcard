namespace Wildcard.Mcp;

/// <summary>
/// Manages one <see cref="WorkspaceIndex"/> per allowed root.
/// Reacts to <see cref="RootsProvider.RootsChanged"/> to add/remove indexes.
/// </summary>
public sealed class WorkspaceIndexManager : IDisposable
{
    private readonly RootsProvider _rootsProvider;
    private readonly object _lock = new();
    private Dictionary<string, WorkspaceIndex> _indexes;

    public WorkspaceIndexManager(RootsProvider rootsProvider)
    {
        _rootsProvider = rootsProvider;
        _indexes = BuildIndexes(rootsProvider.AllowedRoots);
        _rootsProvider.RootsChanged += OnRootsChanged;
    }

    /// <summary>
    /// Returns indexed file paths matching the glob pattern, delegating to the
    /// index whose root contains <paramref name="baseDir"/>.
    /// Returns null if no index covers the given directory.
    /// </summary>
    public IEnumerable<string>? MatchGlob(string pattern, string baseDir, string[]? excludePaths)
    {
        var index = FindIndex(baseDir);
        return index?.MatchGlob(pattern, baseDir, excludePaths);
    }

    /// <summary>Proactively notify that a file was written, routing to the correct index.</summary>
    public void NotifyFileWritten(string absolutePath)
    {
        var index = FindIndex(Path.GetDirectoryName(absolutePath) ?? absolutePath);
        index?.NotifyFileWritten(absolutePath);
    }

    /// <summary>Proactively notify that a file was deleted, routing to the correct index.</summary>
    public void NotifyFileDeleted(string absolutePath)
    {
        var index = FindIndex(Path.GetDirectoryName(absolutePath) ?? absolutePath);
        index?.NotifyFileDeleted(absolutePath);
    }

    public void Dispose()
    {
        _rootsProvider.RootsChanged -= OnRootsChanged;
        lock (_lock)
        {
            foreach (var idx in _indexes.Values)
                idx.Dispose();
            _indexes.Clear();
        }
    }

    internal async Task<WorkspaceIndex?> GetIndexAsync(string baseDir)
    {
        var index = FindIndex(baseDir);
        if (index is not null)
            await index.WaitForReady();
        return index;
    }

    private WorkspaceIndex? FindIndex(string baseDir)
    {
        var normalized = Path.GetFullPath(baseDir);
        Dictionary<string, WorkspaceIndex> indexes;
        lock (_lock) { indexes = _indexes; }

        foreach (var (root, index) in indexes)
        {
            if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return null;
    }

    private void OnRootsChanged(IReadOnlyList<string> newRoots)
    {
        var newSet = new HashSet<string>(newRoots, StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            // Dispose indexes for removed roots
            var toRemove = _indexes.Keys.Where(k => !newSet.Contains(k)).ToList();
            foreach (var key in toRemove)
            {
                _indexes[key].Dispose();
                _indexes.Remove(key);
            }

            // Create indexes for new roots
            foreach (var root in newRoots)
            {
                if (!_indexes.ContainsKey(root))
                {
                    var dir = root.TrimEnd(Path.DirectorySeparatorChar);
                    if (Directory.Exists(dir))
                        _indexes[root] = new WorkspaceIndex(dir);
                }
            }
        }
    }

    private static Dictionary<string, WorkspaceIndex> BuildIndexes(IReadOnlyList<string> roots)
    {
        var indexes = new Dictionary<string, WorkspaceIndex>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var dir = root.TrimEnd(Path.DirectorySeparatorChar);
            if (Directory.Exists(dir))
                indexes[root] = new WorkspaceIndex(dir);
        }

        return indexes;
    }
}

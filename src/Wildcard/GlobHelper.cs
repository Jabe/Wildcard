namespace Wildcard;

/// <summary>
/// Utility methods for glob pattern analysis (watch mode support).
/// </summary>
public static class GlobHelper
{
    /// <summary>
    /// Determines if a glob pattern requires recursive (subdirectory) watching.
    /// Returns true when the pattern contains <c>**</c> (inherently recursive) or has two or more
    /// wildcard path segments (e.g. <c>*/*.cs</c>), indicating directory-level wildcards that
    /// need <c>FileSystemWatcher.IncludeSubdirectories = true</c>.
    /// </summary>
    public static bool NeedsRecursiveWatch(string globPattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(globPattern);
        var normalized = globPattern.Replace('\\', '/');

        // ** is inherently recursive
        if (normalized.Contains("**"))
            return true;

        // Multiple wildcard segments (e.g. */*.cs) need subdirectory watching
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int remaining = 0;
        bool seenWildcard = false;
        foreach (var part in parts)
        {
            if (!seenWildcard && part.IndexOfAny(['*', '?', '[', '{']) >= 0)
                seenWildcard = true;
            if (seenWildcard) remaining++;
        }
        return remaining >= 2;
    }

    /// <summary>
    /// Extracts the literal directory prefix from a glob pattern for use as a watch base directory.
    /// Segments before the first wildcard are treated as literal path components.
    /// </summary>
    public static string GetWatchBaseDirectory(string globPattern, string cwd)
    {
        ArgumentException.ThrowIfNullOrEmpty(globPattern);
        var normalized = globPattern.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var baseParts = new List<string>();
        foreach (var part in parts)
        {
            if (part.IndexOfAny(['*', '?', '[', '{']) >= 0) break;
            baseParts.Add(part);
        }
        if (baseParts.Count == 0) return cwd;

        // Reconstruct with the original root for absolute paths
        var basePath = Path.Combine([.. baseParts]);
        if (Path.IsPathRooted(normalized))
            basePath = Path.GetPathRoot(normalized)! + basePath;
        return Path.GetFullPath(basePath, cwd);
    }
}

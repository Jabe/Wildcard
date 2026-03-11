namespace Wildcard;

/// <summary>
/// Parses and evaluates .gitignore rules for filtering files and directories during glob traversal.
/// Supports hierarchical .gitignore files (root → nested), negation (!), directory-only patterns (/),
/// and standard glob syntax (*, **, ?, [abc]).
/// </summary>
public sealed class GitignoreFilter
{
    private readonly List<IgnoreRule> _rules = [];
    private string? _gitRoot;

    /// <summary>
    /// Finds the git repository root by walking up from the given directory looking for a .git directory.
    /// Returns null if not inside a git repository.
    /// </summary>
    public static string? FindGitRoot(string startDirectory)
    {
        var dir = startDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// Creates a GitignoreFilter by loading .gitignore files from the git root.
    /// Loads: global gitignore, .git/info/exclude, and root .gitignore.
    /// </summary>
    public static GitignoreFilter? LoadFromGitRoot(string gitRoot)
    {
        var filter = new GitignoreFilter { _gitRoot = gitRoot };
        bool hasRules = false;

        // 1. Global gitignore (~/.config/git/ignore)
        var globalIgnore = GetGlobalGitignorePath();
        if (globalIgnore is not null && File.Exists(globalIgnore))
        {
            filter.AddRules(globalIgnore, gitRoot);
            hasRules = true;
        }

        // 2. .git/info/exclude
        var exclude = Path.Combine(gitRoot, ".git", "info", "exclude");
        if (File.Exists(exclude))
        {
            filter.AddRules(exclude, gitRoot);
            hasRules = true;
        }

        // 3. Root .gitignore
        var rootIgnore = Path.Combine(gitRoot, ".gitignore");
        if (File.Exists(rootIgnore))
        {
            filter.AddRules(rootIgnore, gitRoot);
            hasRules = true;
        }

        return hasRules ? filter : null;
    }

    /// <summary>
    /// Loads additional rules from a .gitignore file found in a subdirectory.
    /// Call this when entering directories during traversal.
    /// Returns the number of rules added (for removal when leaving the directory).
    /// </summary>
    public int AddRulesFromDirectory(string directory)
    {
        var gitignorePath = Path.Combine(directory, ".gitignore");
        if (!File.Exists(gitignorePath)) return 0;
        return AddRules(gitignorePath, directory);
    }

    /// <summary>
    /// Removes the last N rules (used when leaving a directory to restore parent state).
    /// </summary>
    public void RemoveLastRules(int count)
    {
        if (count > 0 && count <= _rules.Count)
            _rules.RemoveRange(_rules.Count - count, count);
    }

    /// <summary>
    /// Returns true if the given path should be ignored.
    /// </summary>
    /// <param name="relativePath">Path relative to the git root, using / separators.</param>
    /// <param name="isDirectory">Whether the path is a directory.</param>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        bool ignored = false;

        // Last matching rule wins (supports negation override)
        foreach (var rule in _rules)
        {
            if (rule.DirectoryOnly && !isDirectory) continue;

            if (rule.FullPathPattern is not null && rule.FullPathPattern.IsMatch(relativePath.AsSpan()))
            {
                ignored = !rule.Negated;
            }
            else if (rule.BasenamePattern is not null)
            {
                // Basename-only patterns match against the filename at any level
                var fileName = Path.GetFileName(relativePath.AsSpan());
                if (rule.BasenamePattern.IsMatch(fileName))
                    ignored = !rule.Negated;
            }
        }

        return ignored;
    }

    private int AddRules(string gitignorePath, string baseDirectory)
    {
        // Compute the relative prefix for path-relative patterns in nested .gitignore files
        string? relativePrefix = null;
        if (_gitRoot is not null && !string.Equals(baseDirectory, _gitRoot, StringComparison.Ordinal))
        {
            relativePrefix = Path.GetRelativePath(_gitRoot, baseDirectory).Replace('\\', '/');
        }

        int added = 0;
        foreach (var line in File.ReadLines(gitignorePath))
        {
            var rule = ParseRule(line, relativePrefix);
            if (rule is not null)
            {
                _rules.Add(rule);
                added++;
            }
        }
        return added;
    }

    private static IgnoreRule? ParseRule(string line, string? relativePrefix)
    {
        // Trim trailing whitespace (but not leading — leading spaces are significant in git)
        var span = line.AsSpan().TrimEnd();

        // Skip empty lines and comments
        if (span.Length == 0 || span[0] == '#')
            return null;

        // Handle negation
        bool negated = false;
        if (span[0] == '!')
        {
            negated = true;
            span = span[1..];
            if (span.Length == 0) return null;
        }

        // Handle leading backslash escape (e.g., \# to match literal #)
        if (span[0] == '\\' && span.Length > 1 && span[1] is '#' or '!')
            span = span[1..];

        // Check directory-only (trailing /)
        bool directoryOnly = false;
        if (span[^1] == '/')
        {
            directoryOnly = true;
            span = span[..^1];
            if (span.Length == 0) return null;
        }

        // Check if pattern contains a slash (affects matching behavior)
        var patternStr = span.ToString();
        bool hasSlash = patternStr.Contains('/');

        // Leading / means relative to the .gitignore location
        if (patternStr.StartsWith('/'))
            patternStr = patternStr[1..];

        WildcardPattern? fullPathPattern = null;
        WildcardPattern? basenamePattern = null;

        if (hasSlash)
        {
            // Pattern with slashes matches against the full relative path.
            // For nested .gitignore files, prefix with the directory's relative path from git root.
            if (relativePrefix is not null)
                patternStr = relativePrefix + "/" + patternStr;

            fullPathPattern = WildcardPattern.Compile(patternStr);
        }
        else
        {
            // Basename-only pattern — match against filename at any level
            basenamePattern = WildcardPattern.Compile(patternStr);
        }

        return new IgnoreRule(fullPathPattern, basenamePattern, negated, directoryOnly);
    }

    private static string? GetGlobalGitignorePath()
    {
        // Check XDG_CONFIG_HOME first, then default to ~/.config
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdgConfig))
            return Path.Combine(xdgConfig, "git", "ignore");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            return Path.Combine(home, ".config", "git", "ignore");

        return null;
    }

    /// <summary>
    /// Creates a shallow clone of this filter. Rules are shared (immutable records), list is copied.
    /// </summary>
    internal GitignoreFilter Clone()
    {
        var clone = new GitignoreFilter { _gitRoot = _gitRoot };
        clone._rules.AddRange(_rules);
        return clone;
    }

    private sealed record IgnoreRule(WildcardPattern? FullPathPattern, WildcardPattern? BasenamePattern, bool Negated, bool DirectoryOnly);
}
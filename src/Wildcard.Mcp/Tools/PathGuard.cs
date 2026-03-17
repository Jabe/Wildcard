namespace Wildcard.Mcp.Tools;

internal static class PathGuard
{
    private static readonly string AllowedRoot = Path.GetFullPath(Directory.GetCurrentDirectory()) + Path.DirectorySeparatorChar;

    /// <summary>
    /// Resolves the base directory and ensures it falls within the server's working directory.
    /// Returns (resolvedPath, null) on success, or ("", errorMessage) if the path escapes the allowed root.
    /// </summary>
    public static (string Path, string? Error) Resolve(string? baseDirectory)
    {
        var resolved = Path.GetFullPath(baseDirectory ?? Directory.GetCurrentDirectory());

        if (!resolved.StartsWith(AllowedRoot, StringComparison.Ordinal) &&
            !string.Equals(resolved + Path.DirectorySeparatorChar, AllowedRoot, StringComparison.Ordinal))
        {
            return ("", $"Access denied: '{resolved}' is outside the allowed root '{AllowedRoot.TrimEnd(Path.DirectorySeparatorChar)}'.");
        }

        return (resolved, null);
    }
}

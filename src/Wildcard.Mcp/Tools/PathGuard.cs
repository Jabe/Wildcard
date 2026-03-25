namespace Wildcard.Mcp.Tools;

internal static class PathGuard
{
    private static readonly string AllowedRoot = Path.GetFullPath(Directory.GetCurrentDirectory()) + Path.DirectorySeparatorChar;

    /// <summary>
    /// Resolves a path and ensures it falls within the server's working directory.
    /// Throws <see cref="UnauthorizedAccessException"/> if the path escapes the allowed root.
    /// </summary>
    public static string Resolve(string? baseDirectory)
    {
        // Use frozen root (not live CWD) when no directory is specified
        var resolved = Path.GetFullPath(baseDirectory ?? AllowedRoot);

        // Resolve symlinks (including dangling ones) to prevent escape via symlink pointing outside root.
        // FileInfo/DirectoryInfo.LinkTarget works on dangling symlinks — no Exists check needed.
        // Note: this reduces but does not eliminate TOCTOU risk — a symlink could be swapped
        // between resolve and use. Full elimination would require kernel-level open-then-check
        // (e.g. O_BENEATH), which .NET does not expose.
        var fileInfo = new FileInfo(resolved);

        if (fileInfo.LinkTarget is not null)
        {
            resolved = fileInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? resolved;
        }
        else
        {
            var dirInfo = new DirectoryInfo(resolved);

            if (dirInfo.LinkTarget is not null)
                resolved = dirInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? resolved;
        }

        if (!resolved.StartsWith(AllowedRoot, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(resolved + Path.DirectorySeparatorChar, AllowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Access denied: path is outside the allowed root.");
        }

        return resolved;
    }
}

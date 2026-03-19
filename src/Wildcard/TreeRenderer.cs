using System.Text;

namespace Wildcard;

/// <summary>
/// Renders a flat list of relative paths as an indented ASCII tree.
/// Shared by the MCP glob tool and the wcg CLI.
/// </summary>
public static class TreeRenderer
{
    /// <summary>
    /// Builds an ASCII tree from a set of forward-slash-delimited relative paths.
    /// </summary>
    /// <param name="paths">Relative file paths (forward-slash separated).</param>
    /// <param name="maxDepth">Maximum depth to render (1 = top-level entries only). 0 or negative means unlimited.</param>
    /// <returns>Formatted tree string.</returns>
    public static string Render(IEnumerable<string> paths, int maxDepth = 5)
    {
        var root = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        int totalFiles = 0;

        foreach (var path in paths)
        {
            totalFiles++;
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = root;

            for (int i = 0; i < parts.Length; i++)
            {
                bool isLast = i == parts.Length - 1;
                if (!current.TryGetValue(parts[i], out var child))
                {
                    if (isLast)
                    {
                        current[parts[i]] = null; // leaf (file)
                    }
                    else
                    {
                        var dir = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        current[parts[i]] = dir;
                        current = dir;
                    }
                }
                else if (!isLast)
                {
                    if (child is SortedDictionary<string, object?> existing)
                    {
                        current = existing;
                    }
                    else
                    {
                        // Was a file leaf, now needed as a directory — promote (original file entry is lost,
                        // but glob results won't produce a path that is both a file and a directory prefix)
                        var dir = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        current[parts[i]] = dir;
                        current = dir;
                    }
                }
                else
                {
                    // Duplicate path or file that's also a dir prefix — keep existing
                    break;
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine(".");
        RenderNode(sb, root, "", maxDepth > 0 ? maxDepth : int.MaxValue, 0);
        sb.AppendLine($"\n{totalFiles} {(totalFiles == 1 ? "file" : "files")}");
        return sb.ToString();
    }

    private static void RenderNode(StringBuilder sb, SortedDictionary<string, object?> node, string prefix, int maxDepth, int currentDepth)
    {
        var entries = node.ToList();

        for (int i = 0; i < entries.Count; i++)
        {
            bool isLastEntry = i == entries.Count - 1;
            var connector = isLastEntry ? "└── " : "├── ";
            var childPrefix = isLastEntry ? "    " : "│   ";

            var (name, child) = entries[i];

            if (child is SortedDictionary<string, object?> dir)
            {
                sb.Append(prefix);
                sb.Append(connector);
                sb.Append(name);
                sb.AppendLine("/");

                if (currentDepth + 1 >= maxDepth)
                {
                    if (dir.Count > 0)
                    {
                        sb.Append(prefix);
                        sb.Append(childPrefix);
                        sb.AppendLine("...");
                    }
                }
                else
                {
                    RenderNode(sb, dir, prefix + childPrefix, maxDepth, currentDepth + 1);
                }
            }
            else
            {
                sb.Append(prefix);
                sb.Append(connector);
                sb.AppendLine(name);
            }
        }
    }
}

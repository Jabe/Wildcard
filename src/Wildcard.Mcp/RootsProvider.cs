using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wildcard.Mcp;

/// <summary>
/// Manages allowed workspace roots obtained from the MCP client via the roots protocol.
/// Replaces the static PathGuard approach — roots are resolved dynamically from the client
/// and can change at runtime via <c>notifications/roots/list_changed</c>.
/// Requires the client to declare roots capability — no fallback to cwd.
/// </summary>
public sealed class RootsProvider : IDisposable
{
    private volatile IReadOnlyList<string> _allowedRoots = [];
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;
    private IAsyncDisposable? _notificationRegistration;

    /// <summary>Fires when the allowed roots are updated (e.g. from a roots/list_changed notification).</summary>
    internal event Action<IReadOnlyList<string>>? RootsChanged;

    /// <summary>Normalized absolute root paths, each with a trailing separator.</summary>
    public IReadOnlyList<string> AllowedRoots => _allowedRoots;

    /// <summary>The first allowed root (without trailing separator) — used as default when <c>base_directory</c> is null.</summary>
    public string DefaultRoot => _allowedRoots is { Count: > 0 }
        ? _allowedRoots[0].TrimEnd(Path.DirectorySeparatorChar)
        : throw new InvalidOperationException("No workspace roots configured. Ensure EnsureInitializedAsync has been called.");

    /// <summary>
    /// Lazily initializes roots from the MCP client on the first call.
    /// Thread-safe via double-checked locking with <see cref="SemaphoreSlim"/>.
    /// </summary>
    public async ValueTask EnsureInitializedAsync(McpServer server, CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            if (server.ClientCapabilities?.Roots is null)
                throw new InvalidOperationException("Client does not support MCP roots capability. The wildcard server requires the client to declare workspace roots.");

            var result = await server.RequestRootsAsync(new ListRootsRequestParams(), ct);
            UpdateRoots(result.Roots);

            if (_allowedRoots.Count == 0)
                throw new InvalidOperationException("Client returned no valid file:// roots.");

            if (server.ClientCapabilities.Roots.ListChanged == true)
            {
                _notificationRegistration = server.RegisterNotificationHandler(
                    NotificationMethods.RootsListChangedNotification,
                    async (_, ct2) =>
                    {
                        var updated = await server.RequestRootsAsync(new ListRootsRequestParams(), ct2);
                        UpdateRoots(updated.Roots);
                    });
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Resolves a path and ensures it falls within at least one allowed root.
    /// Throws <see cref="UnauthorizedAccessException"/> if the path escapes all roots.
    /// </summary>
    public string Resolve(string? baseDirectory)
    {
        var resolved = Path.GetFullPath(baseDirectory ?? DefaultRoot);

        // Resolve symlinks (including dangling ones) to prevent escape via symlink pointing outside root.
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

        var roots = _allowedRoots;
        foreach (var root in roots)
        {
            if (resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(resolved + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase))
            {
                return resolved;
            }
        }

        throw new UnauthorizedAccessException("Access denied: path is outside the allowed root.");
    }

    /// <summary>Sets allowed roots directly — for testing only. Also marks the provider as initialized.</summary>
    internal void SetRoots(IReadOnlyList<string> absolutePaths)
    {
        var normalized = absolutePaths
            .Select(p => Path.GetFullPath(p) is var fp && fp.EndsWith(Path.DirectorySeparatorChar) ? fp : fp + Path.DirectorySeparatorChar)
            .ToArray();

        _allowedRoots = normalized;
        _initialized = true;
        RootsChanged?.Invoke(_allowedRoots);
    }

    public void Dispose()
    {
        if (_notificationRegistration is not null)
        {
            _notificationRegistration.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _notificationRegistration = null;
        }

        _initLock.Dispose();
    }

    private void UpdateRoots(IList<Root> roots)
    {
        var paths = new List<string>();

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root.Uri)) continue;

            try
            {
                var uri = new Uri(root.Uri);
                if (!uri.IsFile) continue;

                var localPath = Path.GetFullPath(uri.LocalPath);
                if (!localPath.EndsWith(Path.DirectorySeparatorChar))
                    localPath += Path.DirectorySeparatorChar;

                paths.Add(localPath);
            }
            catch (UriFormatException)
            {
                // Skip malformed URIs
            }
        }

        _allowedRoots = paths.AsReadOnly();
        RootsChanged?.Invoke(_allowedRoots);
    }
}

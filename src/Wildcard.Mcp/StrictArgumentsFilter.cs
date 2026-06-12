using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wildcard.Mcp;

/// <summary>
/// Call-tool filter that rejects unknown argument keys instead of silently dropping them.
/// Catches schema skew (a client's stale cached tool definition vs. the server's current
/// signature) on the very first call: a silently-ignored argument produces plausible
/// garbage the LLM believes, while a loud error gets corrected immediately.
/// </summary>
public static class StrictArgumentsFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Apply(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (request, cancellationToken) =>
        {
            // MatchedPrimitive is populated by the SDK before user filters run.
            // Unknown tool names fall through to the SDK's own error handling.
            if (request.MatchedPrimitive is McpServerTool tool &&
                request.Params?.Arguments is { Count: > 0 } arguments)
            {
                var known = KnownArgumentNames(tool.ProtocolTool.InputSchema);
                if (known is not null)
                {
                    var unknown = arguments.Keys.Where(k => !known.Contains(k)).ToList();
                    if (unknown.Count > 0)
                    {
                        var message =
                            $"Unknown argument{(unknown.Count > 1 ? "s" : "")} for tool '{request.Params.Name}': " +
                            $"{string.Join(", ", unknown.Select(k => $"'{k}'"))}. " +
                            $"Known arguments: {string.Join(", ", known.Order())}. " +
                            "If these came from a cached tool definition, re-list the tools — the schema may have changed.";

                        return ValueTask.FromResult(new CallToolResult
                        {
                            IsError = true,
                            Content = [new TextContentBlock { Text = message }],
                        });
                    }
                }
            }

            return next(request, cancellationToken);
        };

    /// <summary>
    /// Extracts the argument names from a tool's JSON input schema.
    /// Returns null when the schema declares no properties object — in that case
    /// nothing can be validated and all arguments pass through.
    /// </summary>
    public static HashSet<string>? KnownArgumentNames(JsonElement inputSchema)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object ||
            !inputSchema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
            names.Add(property.Name);
        return names;
    }
}

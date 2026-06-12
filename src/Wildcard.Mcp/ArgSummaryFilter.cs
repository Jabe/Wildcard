using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wildcard.Mcp;

/// <summary>
/// Call-tool filter that prepends a one-line echo of the call arguments to the tool's
/// text output, e.g. <c>[pattern="**/*.cs", ignore_case=true]</c>. Lets the user audit
/// what the client actually sent without opening the raw request — only explicitly
/// passed arguments appear, so defaults stay out of the line.
/// Off by default; enabled via the --summary CLI flag.
/// </summary>
public static class ArgSummaryFilter
{
    // Default encoder escapes non-ASCII and HTML-sensitive chars — too noisy for a human-facing echo.
    private static readonly JsonSerializerOptions RenderOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Apply(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);

            for (int i = 0; i < result.Content.Count; i++)
            {
                if (result.Content[i] is TextContentBlock text)
                {
                    result.Content[i] = new TextContentBlock { Text = Summarize(request.Params?.Arguments) + text.Text };
                    break;
                }
            }

            return result;
        };

    public static string Summarize(IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        var sb = new StringBuilder("[");

        if (arguments is not null)
        {
            bool first = true;
            foreach (var (name, value) in arguments)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(name).Append('=').Append(Render(value));
            }
        }

        return sb.Append("]\n").ToString();
    }

    private static string Render(JsonElement value)
    {
        // Strings: truncate before serializing so the closing quote survives;
        // serialization handles quoting and escapes newlines (multi-line find text).
        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString()!;
            return JsonSerializer.Serialize(s.Length > 80 ? s[..77] + "..." : s, RenderOptions);
        }

        var rendered = JsonSerializer.Serialize(value, RenderOptions);
        return rendered.Length > 120 ? rendered[..117] + "..." : rendered;
    }
}

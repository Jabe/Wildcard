using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wildcard.Mcp;

namespace Wildcard.Tests;

public class ArgSummaryFilterTests
{
    // RequestContext requires a live McpServer; a stream transport over Stream.Null suffices.
    private static readonly McpServer Server = McpServer.Create(
        new StreamServerTransport(Stream.Null, Stream.Null),
        new McpServerOptions());

    private static Dictionary<string, JsonElement> Args(params (string Name, object? Value)[] args) =>
        args.ToDictionary(a => a.Name, a => JsonSerializer.SerializeToElement(a.Value));

    // --- Summarize ---

    [Fact]
    public void Summarize_RendersStringsBoolsAndNumbers()
    {
        var line = ArgSummaryFilter.Summarize(Args(("pattern", "**/*.cs"), ("ignore_case", true), ("limit", 10)));
        Assert.Equal("[pattern=\"**/*.cs\", ignore_case=true, limit=10]\n", line);
    }

    [Fact]
    public void Summarize_RendersStringArrays()
    {
        var line = ArgSummaryFilter.Summarize(Args(("content_patterns", new[] { "ERROR", "WARN" })));
        Assert.Equal("[content_patterns=[\"ERROR\",\"WARN\"]]\n", line);
    }

    [Fact]
    public void Summarize_TruncatesLongStrings()
    {
        var line = ArgSummaryFilter.Summarize(Args(("find", new string('x', 200))));
        Assert.Equal($"[find=\"{new string('x', 77)}...\"]\n", line);
    }

    [Fact]
    public void Summarize_EscapesMultiLineStrings()
    {
        var line = ArgSummaryFilter.Summarize(Args(("find", "foo\nbar")));
        Assert.Equal("[find=\"foo\\nbar\"]\n", line);
        Assert.DoesNotContain('\n', line.TrimEnd('\n'));
    }

    [Fact]
    public void Summarize_NullOrEmptyArguments_RendersEmptyBrackets()
    {
        Assert.Equal("[]\n", ArgSummaryFilter.Summarize(null));
        Assert.Equal("[]\n", ArgSummaryFilter.Summarize(new Dictionary<string, JsonElement>()));
    }

    // --- Filter behavior ---

    private static RequestContext<CallToolRequestParams> MakeRequest(Dictionary<string, JsonElement>? arguments) =>
        new(Server, new JsonRpcRequest { Method = "tools/call" })
        {
            Params = new CallToolRequestParams { Name = "test_tool", Arguments = arguments },
        };

    [Fact]
    public async Task PrependsSummaryToFirstTextBlock()
    {
        var handler = ArgSummaryFilter.Apply((request, ct) => ValueTask.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = "result text" }],
        }));

        var result = await handler(MakeRequest(Args(("pattern", "*.cs"))), CancellationToken.None);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Equal("[pattern=\"*.cs\"]\nresult text", text);
    }

    [Fact]
    public async Task ErrorResults_AlsoGetSummary()
    {
        var handler = ArgSummaryFilter.Apply((request, ct) => ValueTask.FromResult(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "Access denied." }],
        }));

        var result = await handler(MakeRequest(Args(("base_directory", "/etc"))), CancellationToken.None);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Equal("[base_directory=\"/etc\"]\nAccess denied.", text);
    }

    [Fact]
    public async Task NoTextContent_PassesThroughUnchanged()
    {
        var handler = ArgSummaryFilter.Apply((request, ct) => ValueTask.FromResult(new CallToolResult
        {
            Content = [],
        }));

        var result = await handler(MakeRequest(Args(("pattern", "*.cs"))), CancellationToken.None);

        Assert.Empty(result.Content);
    }
}

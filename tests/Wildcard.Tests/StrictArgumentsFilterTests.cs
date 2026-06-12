using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wildcard.Mcp;

namespace Wildcard.Tests;

public class StrictArgumentsFilterTests
{
    // RequestContext requires a live McpServer; a stream transport over Stream.Null suffices.
    private static readonly McpServer Server = McpServer.Create(
        new StreamServerTransport(Stream.Null, Stream.Null),
        new McpServerOptions());

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement;

    // --- KnownArgumentNames ---

    [Fact]
    public void KnownArgumentNames_ExtractsPropertyNames()
    {
        var schema = Schema("""{"type":"object","properties":{"pattern":{},"find":{},"replace":{}}}""");
        var names = StrictArgumentsFilter.KnownArgumentNames(schema);

        Assert.NotNull(names);
        Assert.Equal(["find", "pattern", "replace"], names.Order());
    }

    [Fact]
    public void KnownArgumentNames_NoProperties_ReturnsNull()
    {
        Assert.Null(StrictArgumentsFilter.KnownArgumentNames(Schema("""{"type":"object"}""")));
        Assert.Null(StrictArgumentsFilter.KnownArgumentNames(Schema("""["not an object"]""")));
    }

    // --- Filter behavior ---

    private static McpServerTool MakeTool() =>
        McpServerTool.Create((string pattern, int limit) => "ok", new McpServerToolCreateOptions { Name = "test_tool" });

    private static RequestContext<CallToolRequestParams> MakeRequest(McpServerTool tool, Dictionary<string, JsonElement> arguments) =>
        new(Server, new JsonRpcRequest { Method = "tools/call" })
        {
            Params = new CallToolRequestParams { Name = "test_tool", Arguments = arguments },
            MatchedPrimitive = tool,
        };

    private static readonly McpRequestHandler<CallToolRequestParams, CallToolResult> InnerHandler =
        (request, ct) => ValueTask.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = "inner reached" }],
        });

    private static string FirstText(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    [Fact]
    public async Task UnknownArgument_IsRejected_WithKnownArgumentList()
    {
        var handler = StrictArgumentsFilter.Apply(InnerHandler);
        var request = MakeRequest(MakeTool(), new()
        {
            ["pattern"] = JsonSerializer.SerializeToElement("*.cs"),
            ["output_mode"] = JsonSerializer.SerializeToElement("content"),
        });

        var result = await handler(request, CancellationToken.None);

        Assert.True(result.IsError);
        var text = FirstText(result);
        Assert.Contains("Unknown argument for tool 'test_tool': 'output_mode'", text);
        Assert.Contains("Known arguments:", text);
        Assert.Contains("pattern", text);
        Assert.Contains("re-list the tools", text);
    }

    [Fact]
    public async Task MultipleUnknownArguments_AllListed()
    {
        var handler = StrictArgumentsFilter.Apply(InnerHandler);
        var request = MakeRequest(MakeTool(), new()
        {
            ["path"] = JsonSerializer.SerializeToElement("src"),
            ["-n"] = JsonSerializer.SerializeToElement(true),
        });

        var result = await handler(request, CancellationToken.None);

        Assert.True(result.IsError);
        var text = FirstText(result);
        Assert.Contains("Unknown arguments", text);
        Assert.Contains("'path'", text);
        Assert.Contains("'-n'", text);
    }

    [Fact]
    public async Task KnownArguments_PassThrough()
    {
        var handler = StrictArgumentsFilter.Apply(InnerHandler);
        var request = MakeRequest(MakeTool(), new()
        {
            ["pattern"] = JsonSerializer.SerializeToElement("*.cs"),
            ["limit"] = JsonSerializer.SerializeToElement(10),
        });

        var result = await handler(request, CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("inner reached", FirstText(result));
    }

    [Fact]
    public async Task NoArguments_PassesThrough()
    {
        var handler = StrictArgumentsFilter.Apply(InnerHandler);
        var request = new RequestContext<CallToolRequestParams>(Server, new JsonRpcRequest { Method = "tools/call" })
        {
            Params = new CallToolRequestParams { Name = "test_tool" },
            MatchedPrimitive = MakeTool(),
        };

        var result = await handler(request, CancellationToken.None);
        Assert.Equal("inner reached", FirstText(result));
    }

    [Fact]
    public async Task NoMatchedPrimitive_PassesThrough_ToSdkErrorHandling()
    {
        var handler = StrictArgumentsFilter.Apply(InnerHandler);
        var request = new RequestContext<CallToolRequestParams>(Server, new JsonRpcRequest { Method = "tools/call" })
        {
            Params = new CallToolRequestParams
            {
                Name = "no_such_tool",
                Arguments = new Dictionary<string, JsonElement> { ["whatever"] = JsonSerializer.SerializeToElement(1) },
            },
        };

        var result = await handler(request, CancellationToken.None);
        Assert.Equal("inner reached", FirstText(result));
    }
}

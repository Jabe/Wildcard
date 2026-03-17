using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using Wildcard.Mcp;

// Prevent thread pool ramp-up delay for I/O-bound parallel workloads
ThreadPool.SetMinThreads(Environment.ProcessorCount * 2, Environment.ProcessorCount);

bool live = args.Contains("--live");

var builder = Host.CreateEmptyApplicationBuilder(settings: null);

if (live)
    builder.Services.AddSingleton(new WorkspaceIndex(Directory.GetCurrentDirectory()));

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "wildcard",
            Version = "0.1.0",
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

await host.RunAsync();

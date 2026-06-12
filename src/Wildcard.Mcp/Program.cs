using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using Wildcard.Mcp;

// Prevent thread pool ramp-up delay for I/O-bound parallel workloads
ThreadPool.SetMinThreads(Environment.ProcessorCount * 2, Environment.ProcessorCount);

bool live = args.Contains("--live");

var builder = Host.CreateEmptyApplicationBuilder(settings: null);

builder.Services.AddSingleton<RootsProvider>();

if (live)
    builder.Services.AddSingleton<WorkspaceIndexManager>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "wildcard",
            Version = typeof(RootsProvider).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithRequestFilters(filters => filters.AddCallToolFilter(StrictArgumentsFilter.Apply));

var host = builder.Build();

await host.RunAsync();

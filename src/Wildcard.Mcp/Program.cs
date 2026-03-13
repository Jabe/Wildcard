using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

// Prevent thread pool ramp-up delay for I/O-bound parallel workloads
ThreadPool.SetMinThreads(Environment.ProcessorCount * 2, Environment.ProcessorCount);

var builder = Host.CreateEmptyApplicationBuilder(settings: null);

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

await builder.Build().RunAsync();

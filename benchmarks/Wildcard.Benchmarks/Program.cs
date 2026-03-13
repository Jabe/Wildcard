using BenchmarkDotNet.Running;
using Wildcard.Benchmarks;

if (args.Length > 0 && args[0] == "--vs-native")
{
    NativeVsWildcardRunner.Run();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(PatternMatchBenchmarks).Assembly).Run(args);

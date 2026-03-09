using BenchmarkDotNet.Running;
using Wildcard.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(PatternMatchBenchmarks).Assembly).Run(args);

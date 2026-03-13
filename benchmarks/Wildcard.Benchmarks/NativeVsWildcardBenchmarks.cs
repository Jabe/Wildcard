using System.Diagnostics;
using System.Threading.Channels;

namespace Wildcard.Benchmarks;

/// <summary>
/// Manual benchmark runner: Unix native tools (find, grep, rg) vs .NET Directory APIs vs Wildcard.
/// Mirrors benchmark-vs-native.sh. Invoked via: dotnet run -- --vs-native
///
/// Target directory defaults to ~/Code. Override with WILDCARD_BENCH_DIR env var.
/// </summary>
public static class NativeVsWildcardRunner
{
    public static void Run()
    {
        var targetDir = Environment.GetEnvironmentVariable("WILDCARD_BENCH_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Code");

        bool rgAvailable = IsToolAvailable("rg");

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("  Benchmark: Unix Native vs .NET vs Wildcard");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine($"  Target : {targetDir}");
        Console.WriteLine($"  rg     : {(rgAvailable ? "available" : "not found — rg benchmarks skipped")}");
        Console.WriteLine();

        // Pre-parse globs and matchers
        var csGlob   = Glob.Parse($"{targetDir}/**/*.cs");
        var jsonGlob = Glob.Parse($"{targetDir}/**/*.json");
        var dllGlob  = Glob.Parse($"{targetDir}/**/bin/**/*.dll");

        var namespaceMatcher = FilePathMatcher.Create("*namespace*");
        var todoMatcher      = FilePathMatcher.Create("*TODO*");
        var errorMatcherI    = FilePathMatcher.Create("*error*", options: new FilePathMatcher.Options { IgnoreCase = true });

        // Warmup
        Console.Write("  Warming up...");
        csGlob.EnumerateMatches().Count();
        Directory.EnumerateFiles(targetDir, "*.cs", SearchOption.AllDirectories).Count();
        RunProcess("find", $"{targetDir} -name \"*.cs\"");
        Console.WriteLine(" done.");
        Console.WriteLine();

        var results = new List<(string Section, string Label, long Ms)>();

        void Bench(string section, string label, Action action)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            results.Add((section, label, sw.ElapsedMilliseconds));
            Console.WriteLine($"  {label,-55} {sw.ElapsedMilliseconds,6} ms");
        }

        // ── 1. Find .cs ──────────────────────────────────────────────────────
        Console.WriteLine("── 1. File Discovery — *.cs ─────────────────────────────────────");
        Bench("1. Find .cs", "find -name '*.cs'",
            () => RunProcess("find", $"{targetDir} -name \"*.cs\""));
        Bench("1. Find .cs", "Directory.EnumerateFiles *.cs",
            () => Directory.EnumerateFiles(targetDir, "*.cs", SearchOption.AllDirectories).Count());
        Bench("1. Find .cs", "wcg **/*.cs (sequential)",
            () => csGlob.EnumerateMatches().Count());
        Bench("1. Find .cs", "wcg **/*.cs (parallel channel)",
            () => DrainChannel(csGlob));

        // ── 2. Find .json ─────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("── 2. File Discovery — *.json ───────────────────────────────────");
        Bench("2. Find .json", "find -name '*.json'",
            () => RunProcess("find", $"{targetDir} -name \"*.json\""));
        Bench("2. Find .json", "Directory.EnumerateFiles *.json",
            () => Directory.EnumerateFiles(targetDir, "*.json", SearchOption.AllDirectories).Count());
        Bench("2. Find .json", "wcg **/*.json (sequential)",
            () => jsonGlob.EnumerateMatches().Count());
        Bench("2. Find .json", "wcg **/*.json (parallel channel)",
            () => DrainChannel(jsonGlob));

        // ── 3. Deep glob .dll ─────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("── 3. Deep Glob — **/bin/**/*.dll ───────────────────────────────");
        Bench("3. Deep .dll", "find -path '*/bin/*.dll'",
            () => RunProcess("find", $"{targetDir} -path \"*/bin/*.dll\""));
        Bench("3. Deep .dll", "EnumerateDirectories bin + EnumerateFiles *.dll", () =>
        {
            int c = 0;
            foreach (var d in Directory.EnumerateDirectories(targetDir, "bin", SearchOption.AllDirectories))
                c += Directory.EnumerateFiles(d, "*.dll", SearchOption.AllDirectories).Count();
            _ = c;
        });
        Bench("3. Deep .dll", "wcg **/bin/**/*.dll (sequential)",
            () => dllGlob.EnumerateMatches().Count());
        Bench("3. Deep .dll", "wcg **/bin/**/*.dll (parallel channel)",
            () => DrainChannel(dllGlob));

        // ── 4. Search 'namespace' in .cs ─────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("── 4. Content Search — 'namespace' in *.cs ──────────────────────");
        Bench("4. namespace", "find *.cs -exec grep -l namespace",
            () => RunProcess("bash", $"-c \"find '{targetDir}' -name '*.cs' -exec grep -l 'namespace' {{}} +\""));
        Bench("4. namespace", "grep -rl namespace --include=*.cs",
            () => RunProcess("grep", $"-rl namespace --include=\"*.cs\" {targetDir}"));
        if (rgAvailable)
            Bench("4. namespace", "rg -l namespace --type cs",
                () => RunProcess("rg", $"-l namespace --type cs {targetDir}"));
        Bench("4. namespace", "EnumerateFiles + ReadLines + Contains", () =>
        {
            int c = 0;
            foreach (var f in Directory.EnumerateFiles(targetDir, "*.cs", SearchOption.AllDirectories))
                if (File.ReadLines(f).Any(l => l.Contains("namespace", StringComparison.Ordinal))) c++;
            _ = c;
        });
        Bench("4. namespace", "wcg -l *namespace* **/*.cs (sequential)", () =>
        {
            int c = 0;
            foreach (var f in csGlob.EnumerateMatches())
                if (namespaceMatcher.ContainsMatch(f)) c++;
            _ = c;
        });
        Bench("4. namespace", "wcg -l *namespace* **/*.cs (parallel pipeline)",
            () => RunParallelContentSearch(csGlob, namespaceMatcher).GetAwaiter().GetResult());

        // ── 5. Search 'TODO' in .cs ───────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("── 5. Content Search — 'TODO' in *.cs ───────────────────────────");
        Bench("5. TODO", "grep -rl TODO --include=*.cs",
            () => RunProcess("grep", $"-rl TODO --include=\"*.cs\" {targetDir}"));
        if (rgAvailable)
            Bench("5. TODO", "rg -l TODO --type cs",
                () => RunProcess("rg", $"-l TODO --type cs {targetDir}"));
        Bench("5. TODO", "EnumerateFiles + ReadLines + Contains", () =>
        {
            int c = 0;
            foreach (var f in Directory.EnumerateFiles(targetDir, "*.cs", SearchOption.AllDirectories))
                if (File.ReadLines(f).Any(l => l.Contains("TODO", StringComparison.Ordinal))) c++;
            _ = c;
        });
        Bench("5. TODO", "wcg -l *TODO* **/*.cs (sequential)", () =>
        {
            int c = 0;
            foreach (var f in csGlob.EnumerateMatches())
                if (todoMatcher.ContainsMatch(f)) c++;
            _ = c;
        });
        Bench("5. TODO", "wcg -l *TODO* **/*.cs (parallel pipeline)",
            () => RunParallelContentSearch(csGlob, todoMatcher).GetAwaiter().GetResult());

        // ── 6. Search 'error' (case-insensitive) in .json ────────────────────
        Console.WriteLine();
        Console.WriteLine("── 6. Content Search — 'error' -i in *.json ─────────────────────");
        Bench("6. error-i", "grep -rli error --include=*.json",
            () => RunProcess("grep", $"-rli error --include=\"*.json\" {targetDir}"));
        if (rgAvailable)
            Bench("6. error-i", "rg -li error --type json",
                () => RunProcess("rg", $"-li error --type json {targetDir}"));
        Bench("6. error-i", "EnumerateFiles + ReadLines + Contains -i", () =>
        {
            int c = 0;
            foreach (var f in Directory.EnumerateFiles(targetDir, "*.json", SearchOption.AllDirectories))
                if (File.ReadLines(f).Any(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))) c++;
            _ = c;
        });
        Bench("6. error-i", "wcg -l -i *error* **/*.json (sequential)", () =>
        {
            int c = 0;
            foreach (var f in jsonGlob.EnumerateMatches())
                if (errorMatcherI.ContainsMatch(f)) c++;
            _ = c;
        });
        Bench("6. error-i", "wcg -l -i *error* **/*.json (parallel pipeline)",
            () => RunParallelContentSearch(jsonGlob, errorMatcherI).GetAwaiter().GetResult());

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("  Done!");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
    }

    private static void RunProcess(string tool, string arguments)
    {
        var psi = new ProcessStartInfo(tool, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
    }

    private static bool IsToolAvailable(string tool)
    {
        try
        {
            var psi = new ProcessStartInfo(tool, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
            return true;
        }
        catch { return false; }
    }

    private static int DrainChannel(Glob glob)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true,
        });
        var producer = Task.Run(() =>
        {
            try { glob.WriteMatchesToChannel(channel.Writer); }
            finally { channel.Writer.Complete(); }
        });
        int count = 0;
        while (channel.Reader.TryRead(out _)) count++;
        producer.Wait();
        while (channel.Reader.TryRead(out _)) count++;
        return count;
    }

    private static async Task<int> RunParallelContentSearch(Glob glob, FilePathMatcher matcher)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            SingleWriter = false,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var producer = Task.Run(() =>
        {
            try { glob.WriteMatchesToChannel(channel.Writer); }
            finally { channel.Writer.Complete(); }
        });
        int count = 0;
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 };
        await Parallel.ForEachAsync(channel.Reader.ReadAllAsync(), parallelOpts, async (file, _) =>
        {
            await Task.CompletedTask;
            if (matcher.ContainsMatch(file))
                Interlocked.Increment(ref count);
        });
        await producer;
        return count;
    }
}

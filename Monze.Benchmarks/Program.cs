using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0].Equals("--soak", StringComparison.OrdinalIgnoreCase))
{
    await CapacitySoakRunner.RunAsync(args[1..]);
    return 0;
}

var (gate, benchmarkArgs) = MicroBenchmarkGate.Parse(args);
var summaries = BenchmarkSwitcher
    .FromAssembly(typeof(MonzeHotPathBenchmarks).Assembly)
    .Run(benchmarkArgs)
    .ToArray();
return gate?.Evaluate(summaries) ?? 0;

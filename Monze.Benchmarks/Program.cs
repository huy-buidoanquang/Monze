using BenchmarkDotNet.Running;

BenchmarkSwitcher
    .FromAssembly(typeof(MonzeHotPathBenchmarks).Assembly)
    .Run(args);

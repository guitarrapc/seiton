﻿using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using System.Reflection;

var config = ManualConfig.CreateMinimumViable()
    .AddDiagnoser(MemoryDiagnoser.Default)
    .AddExporter(MarkdownExporter.Default);

// In Local environment, run the short benchmark.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
{
    config.AddJob(Job.ShortRun);
}

BenchmarkSwitcher.FromAssembly(Assembly.GetEntryAssembly()!).Run(args, config);

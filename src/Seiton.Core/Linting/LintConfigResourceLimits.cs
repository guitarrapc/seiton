using System;

namespace Seiton.Core.Linting;

/// <summary>Central limits for seiton configuration loading and related network / regex behavior.</summary>
public static class LintConfigResourceLimits
{
    /// <summary>Maximum UTF-8 byte length accepted for a single seiton config file or <see cref="LintConfigLibrary.Validate"/> input.</summary>
    public const int MaxConfigUtf8Bytes = 1_048_576;

    /// <summary>Maximum YAML mapping/sequence nesting depth when building the config DOM.</summary>
    public const int MaxYamlNestDepth = 64;

    /// <summary>Maximum scalar keys, scalar values, and compound containers counted while building the config DOM.</summary>
    public const int MaxYamlDomUnits = 50_000;

    /// <summary>Upper bound for <c>network.max-concurrency</c> after normalization: logical processor count, at least <c>1</c>.</summary>
    public static int MaxNetworkConcurrencyCap => Math.Max(1, Environment.ProcessorCount);

    /// <summary>
    /// Default for <c>network.max-concurrency</c> when omitted: bounded by logical processor count
    /// so omitted values never exceed the validation cap (<see cref="MaxNetworkConcurrencyCap"/>).
    /// </summary>
    public static int DefaultNetworkMaxConcurrency => Math.Min(4, MaxNetworkConcurrencyCap);

    /// <summary>Upper bound for <c>network.timeout-seconds</c> after normalization.</summary>
    public const int MaxNetworkTimeoutSeconds = 300;

    /// <summary>Per-match timeout for user-supplied <c>fix.pinning.ignore-actions</c> regular expressions.</summary>
    public static readonly TimeSpan IgnoreActionRegexMatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Per-match timeout for compiled <c>fix.pinning.exclude-branches</c> patterns (literal, but bounded for defense in depth).</summary>
    public static readonly TimeSpan ExcludeBranchRegexMatchTimeout = TimeSpan.FromSeconds(2);
}

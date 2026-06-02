namespace Seiton.Cli;

/// <summary>Controls how much progress information is written to stderr.</summary>
internal enum VerboseLevel
{
    /// <summary>No verbose output.</summary>
    Off = 0,

    /// <summary>Run-level summary: config, discovery, rules, timing, suppression totals.</summary>
    Summary = 1,

    /// <summary>Summary plus per-file checking and per-file results.</summary>
    Files = 2,
}

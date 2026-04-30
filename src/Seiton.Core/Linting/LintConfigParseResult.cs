using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

/// <summary>Raw output of the configuration YAML parser before normalization.</summary>
internal readonly record struct LintConfigParseResult(
    Dictionary<string, RuleConfig> Rules,
    List<LintExclusion> Exclusions,
    FixConfig Fix,
    NetworkConfig Network,
    OutputConfig Output,
    Diagnostic[] Diagnostics);

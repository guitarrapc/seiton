using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

internal readonly record struct LintConfigParseResult(
    Dictionary<string, RuleConfig> Rules,
    List<LintExclusion> Exclusions,
    FixConfig Fix,
    NetworkConfig Network,
    Diagnostic[] Diagnostics);

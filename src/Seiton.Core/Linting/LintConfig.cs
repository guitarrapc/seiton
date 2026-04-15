using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

public sealed class LintConfig
{
    public static LintConfig Empty { get; } = new();

    public byte[]? Utf8Yaml { get; init; }

    public string? FilePath { get; init; }

    public IReadOnlyDictionary<string, RuleOption>? RuleOptions { get; init; }

    public IReadOnlyList<LintExclusion>? Exclusions { get; init; }

    public ExpressionContext ExprContext { get; init; } = ExpressionContext.Empty;
}

public sealed record ExpressionContext(
    IReadOnlyList<string>? EventTypes = null)
{
    public static ExpressionContext Empty { get; } = new();
}

public sealed record RuleOption(bool Enabled = true, DiagnosticSeverity? Severity = null);

public sealed record LintExclusion(
    string FilePattern,
    IReadOnlyList<string> RuleIds,
    string? JobId = null);

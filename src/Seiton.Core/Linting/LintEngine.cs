using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

public sealed class LintEngine
{
    readonly List<IRule> rules = [];

    public LintEngine()
    {
        rules.Add(new SyntaxRule());
    }

    public LintEngine(IEnumerable<IRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        foreach (var rule in rules)
        {
            AddRule(rule);
        }
    }

    public void AddRule(IRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        rules.Add(rule);
    }

    public LintResult Check(byte[] utf8Yaml, string filePath)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var parseResult = WorkflowParser.Parse(utf8Yaml, filePath);
        if (parseResult.HasFatalError || parseResult.Workflow is null)
        {
            return new LintResult(parseResult, parseResult.Diagnostics);
        }

        var diagnostics = new List<Diagnostic>(parseResult.Diagnostics.Length + 8);
        diagnostics.AddRange(parseResult.Diagnostics);

        if (rules.Count == 0)
        {
            return new LintResult(parseResult, diagnostics.ToArray());
        }

        var visitor = new WorkflowVisitor();
        var config = new LintConfig { Utf8Yaml = utf8Yaml };
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            rule.SetConfig(config);
            visitor.AddPass(rule);
        }

        visitor.Visit(parseResult.Workflow);

        for (var i = 0; i < rules.Count; i++)
        {
            diagnostics.AddRange(rules[i].GetDiagnostics());
        }

        return new LintResult(parseResult, diagnostics.ToArray());
    }
}

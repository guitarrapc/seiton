using System.Text;
using System.Text.Json;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Parsing;

namespace Seiton.Playground;

/// <summary>
/// Runs <see cref="LintEngine"/> and serializes diagnostics to a JSON array for the playground UI.
/// </summary>
public static class PlaygroundLintRunner
{
    private static readonly LintEngine Engine = new();
    private static readonly object EngineGate = new();

    /// <summary>Apply fixes for higher-priority rule IDs before broader structural autofixes (otherwise one batch can corrupt YAML).</summary>
    private static readonly string[] FixRuleApplyPriority =
    [
        "deny-write-all",
        "checkout-persist-credentials",
    ];

    /// <summary>Lint runs with fixes enabled so diagnostics can expose <see cref="PlaygroundDiagnosticDto.Fixable"/> metadata.</summary>
    private static readonly LintConfig LintWithFixMetadata = new()
    {
        Fix = new FixConfig { Enabled = true },
    };

    /// <summary>
    /// Parses and lints <paramref name="yamlSource"/> as UTF-8 and returns a JSON array of diagnostics.
    /// </summary>
    /// <param name="yamlSource">Full YAML document text.</param>
    /// <param name="filePath">Virtual path used for document classification (e.g. workflow vs action).</param>
    /// <returns>UTF-8 JSON array of camelCase diagnostic objects.</returns>
    public static string RunToJson(string yamlSource, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlSource);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var utf8Yaml = Encoding.UTF8.GetBytes(yamlSource);
        LintResult result;
        lock (EngineGate)
        {
            result = Engine.Check(utf8Yaml, filePath, LintWithFixMetadata);
        }

        var list = new List<PlaygroundDiagnosticDto>(result.Diagnostics.Length);
        for (var i = 0; i < result.Diagnostics.Length; i++)
        {
            var d = result.Diagnostics[i];
            var loc = d.Location;
            list.Add(new PlaygroundDiagnosticDto
            {
                Message = d.Message,
                Line = loc.StartLine,
                Column = loc.StartColumn,
                Severity = d.Severity.ToString(),
                RuleId = d.RuleId,
                Fixable = d.Fix is not null,
                FixDescription = d.Fix?.Description,
            });
        }

        return JsonSerializer.Serialize(list, PlaygroundJsonSerializerContext.Default.ListPlaygroundDiagnosticDto);
    }

    /// <summary>
    /// Applies autofix-aware diagnostics sequentially (rule preference order below) until none remain or a safety iteration cap is hit.
    /// </summary>
    public static string ApplyAllFixes(string yamlSource, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlSource);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var current = Encoding.UTF8.GetBytes(yamlSource);
        const int maxPasses = 64;
        for (var pass = 0; pass < maxPasses; pass++)
        {
            lock (EngineGate)
            {
                var result = Engine.Check(current, filePath, LintWithFixMetadata);
                if (!result.HasFixableDiagnostics)
                {
                    return Encoding.UTF8.GetString(current);
                }

                var filtered = CollectAutoApplicableFixes(result.FixableDiagnostics);
                if (filtered.Length == 0)
                {
                    // Still has diagnostics with fixes attached, but none we auto-apply here (see CollectAutoApplicableFixes).
                    return Encoding.UTF8.GetString(current);
                }

                var diag = PickNextDiagnosticToApply(filtered);
                current = FixEngine.Apply(current, new[] { diag });
            }
        }

        return Encoding.UTF8.GetString(current);
    }

    private static Diagnostic[] CollectAutoApplicableFixes(Diagnostic[] fixables)
    {
        if (fixables.Length == 0)
        {
            return [];
        }

        var list = new List<Diagnostic>(fixables.Length);
        for (var i = 0; i < fixables.Length; i++)
        {
            var d = fixables[i];
            if (d.RuleId == "deny-read-all")
            {
                continue;
            }

            list.Add(d);
        }

        return list.Count == 0 ? [] : list.ToArray();
    }

    private static Diagnostic PickNextDiagnosticToApply(Diagnostic[] fixables)
    {
        for (var p = 0; p < FixRuleApplyPriority.Length; p++)
        {
            var want = FixRuleApplyPriority[p];
            for (var i = 0; i < fixables.Length; i++)
            {
                if (fixables[i].RuleId == want)
                {
                    return fixables[i];
                }
            }
        }

        return fixables[0];
    }
}

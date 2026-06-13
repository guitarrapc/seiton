using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed partial class AvailabilityCSharpGenerator
{
    private static readonly IReadOnlyDictionary<string, int> ContextOrder = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["github"] = 0,
        ["inputs"] = 1,
        ["vars"] = 2,
        ["needs"] = 3,
        ["strategy"] = 4,
        ["matrix"] = 5,
        ["job"] = 6,
        ["runner"] = 7,
        ["env"] = 8,
        ["secrets"] = 9,
        ["steps"] = 10,
        ["jobs"] = 11,
    };

    // Level order for grouping enum values: workflow → workflow_call → job → step
    private static int GetLevelOrder(string enumName)
    {
        if (enumName.StartsWith("Step", StringComparison.Ordinal)) return 3;
        if (enumName.StartsWith("Job", StringComparison.Ordinal)) return 2;
        if (enumName.StartsWith("WorkflowCall", StringComparison.Ordinal)) return 1;
        return 0; // workflow-level
    }

    public string Generate(AvailabilityModel model)
    {
        var entries = model.Entries
            .Select(e => (EnumName: ToEnumName(e.WorkflowKey), WorkflowKey: e.WorkflowKey, Contexts: Order(e.Contexts)))
            .OrderBy(static e => GetLevelOrder(e.EnumName))
            .ThenBy(static e => e.EnumName, StringComparer.Ordinal)
            .ToList();

        var entriesByEnumName = entries
            .OrderBy(static e => e.EnumName, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-availability");

        // Emit enum in Seiton.Core.Parsing namespace
        sb.AppendLine("namespace Seiton.Core.Parsing");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Identifies which part of the workflow an expression appears in, for context-sensitive validation.</summary>");
        sb.AppendLine("    public enum ExpressionValidationContext");
        sb.AppendLine("    {");
        foreach (var (enumName, _, _) in entries)
        {
            sb.AppendLine($"        {enumName},");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Emit Availability class
        sb.AppendLine("namespace Seiton.Core.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    using Seiton.Core.Parsing;");
        sb.AppendLine();
        sb.AppendLine("    public static class Availability");
        sb.AppendLine("    {");

        // Per-key arrays
        var first = true;
        foreach (var (enumName, _, contexts) in entries)
        {
            if (!first) sb.AppendLine();
            first = false;
            AppendArray(sb, $"{enumName}Roots", contexts);
        }

        // IsRootContextAvailable
        sb.AppendLine();
        sb.AppendLine("        public static bool IsRootContextAvailable(ExpressionValidationContext context, ReadOnlySpan<byte> rootName)");
        sb.AppendLine("        {");
        sb.AppendLine("            return context switch");
        sb.AppendLine("            {");
        foreach (var (enumName, _, _) in entries)
        {
            sb.AppendLine($"                ExpressionValidationContext.{enumName} => Contains({enumName}Roots, rootName),");
        }
        sb.AppendLine("                _ => false,");
        sb.AppendLine("            };");
        sb.AppendLine("        }");

        // IsStepLevel
        var stepEntries = entries.Where(static e => e.EnumName.StartsWith("Step", StringComparison.Ordinal)).ToList();
        sb.AppendLine();
        sb.AppendLine("        public static bool IsStepLevel(ExpressionValidationContext context)");
        sb.AppendLine("        {");
        sb.Append("            return context is ");
        for (var i = 0; i < stepEntries.Count; i++)
        {
            if (i > 0) sb.Append("                or ");
            sb.Append($"ExpressionValidationContext.{stepEntries[i].EnumName}");
            sb.AppendLine(i < stepEntries.Count - 1 ? "" : ";");
        }
        sb.AppendLine("        }");

        // GetContextText — parser-level descriptive text (appended with " expressions" at call site)
        sb.AppendLine();
        sb.AppendLine("        public static string GetContextText(ExpressionValidationContext context)");
        sb.AppendLine("        {");
        sb.AppendLine("            return context switch");
        sb.AppendLine("            {");
        foreach (var (enumName, workflowKey, _) in entries)
        {
            sb.AppendLine($"                ExpressionValidationContext.{enumName} => \"{GetParserCategoryText(enumName, workflowKey)}\",");
        }
        sb.AppendLine("                _ => \"unknown\",");
        sb.AppendLine("            };");
        sb.AppendLine("        }");

        // GetLintCategoryText — lint-level collapsed text (appended with " scope" at call site)
        sb.AppendLine();
        sb.AppendLine("        public static string GetLintCategoryText(ExpressionValidationContext context)");
        sb.AppendLine("        {");
        sb.AppendLine("            return context switch");
        sb.AppendLine("            {");
        foreach (var (enumName, workflowKey, _) in entriesByEnumName)
        {
            sb.AppendLine($"                ExpressionValidationContext.{enumName} => \"{GetLintCategoryText(enumName, workflowKey)}\",");
        }
        sb.AppendLine("                _ => \"unknown\",");
        sb.AppendLine("            };");
        sb.AppendLine("        }");

        // Contains helper
        sb.AppendLine();
        sb.AppendLine("        static bool Contains(byte[][] table, ReadOnlySpan<byte> name)");
        sb.AppendLine("        {");
        sb.AppendLine("            for (var i = 0; i < table.Length; i++)");
        sb.AppendLine("            {");
        sb.AppendLine("                if (name.SequenceEqual(table[i]))");
        sb.AppendLine("                {");
        sb.AppendLine("                    return true;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            return false;");
        sb.AppendLine("        }");

        // GetAvailableRoots
        sb.AppendLine();
        sb.AppendLine("        public static byte[][] GetAvailableRoots(ExpressionValidationContext context)");
        sb.AppendLine("        {");
        sb.AppendLine("            return context switch");
        sb.AppendLine("            {");
        foreach (var (enumName, _, _) in entriesByEnumName)
        {
            sb.AppendLine($"                ExpressionValidationContext.{enumName} => {enumName}Roots,");
        }
        sb.AppendLine("                _ => [],");
        sb.AppendLine("            };");
        sb.AppendLine("        }");

        // FormatAvailableContexts
        sb.AppendLine();
        sb.AppendLine("        public static string FormatAvailableContexts(ExpressionValidationContext context)");
        sb.AppendLine("        {");
        sb.AppendLine("            var roots = GetAvailableRoots(context);");
        sb.AppendLine("            if (roots.Length == 0) return \"no context is available here\";");
        sb.AppendLine("            var sb = new System.Text.StringBuilder(\"available contexts are \");");
        sb.AppendLine("            for (var i = 0; i < roots.Length; i++)");
        sb.AppendLine("            {");
        sb.AppendLine("                if (i > 0) sb.Append(\", \");");
        sb.AppendLine("                sb.Append('\\\"');");
        sb.AppendLine("                sb.Append(System.Text.Encoding.UTF8.GetString(roots[i]));");
        sb.AppendLine("                sb.Append('\\\"');");
        sb.AppendLine("            }");
        sb.AppendLine("            return sb.ToString();");
        sb.AppendLine("        }");

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return TextNormalization.NormalizeToLf(sb.ToString());
    }

    /// <summary>Converts a workflow key like "jobs.&lt;job_id&gt;.steps.run" to an enum name like "StepRun".</summary>
    internal static string ToEnumName(string workflowKey)
    {
        var segments = workflowKey.Split('.');
        var filtered = segments.Where(static s => !s.StartsWith('<')).ToList();

        string prefix;
        int skipCount;

        if (filtered.Count >= 2 && filtered[0] == "on" && filtered[1] == "workflow_call")
        {
            prefix = "WorkflowCall";
            skipCount = 2;
        }
        else if (filtered.Count >= 2 && filtered[0] == "jobs" && filtered[1] == "steps")
        {
            prefix = "Step";
            skipCount = 2;
        }
        else if (filtered.Count >= 1 && filtered[0] == "jobs")
        {
            prefix = "Job";
            skipCount = 1;
        }
        else
        {
            prefix = "";
            skipCount = 0;
        }

        var remaining = filtered.Skip(skipCount).Select(static s => ToPascalCase(s));
        return prefix + string.Concat(remaining);
    }

    /// <summary>Parser-level category text, used in "context 'X' is not available in {text} expressions".</summary>
    private static string GetParserCategoryText(string enumName, string workflowKey)
    {
        // Preserve backward-compatible text for special cases
        if (enumName == "StepIf") return "step if";
        if (enumName == "JobIf") return "job if";
        if (enumName == "JobStrategy") return "strategy";
        if (enumName == "JobOutputs") return "job output";
        if (enumName == "JobSecrets") return "reusable workflow call secrets";
        if (enumName.StartsWith("WorkflowCall", StringComparison.Ordinal)) return "workflow_call";

        // Level-based defaults
        if (enumName.StartsWith("Step", StringComparison.Ordinal)) return "step";
        if (enumName.StartsWith("Job", StringComparison.Ordinal)) return "job";
        return "workflow";
    }

    /// <summary>Lint-level category text, used in "{sink} expression references undefined context 'X' in {text} scope".</summary>
    private static string GetLintCategoryText(string enumName, string workflowKey)
    {
        if (enumName == "JobOutputs") return "job output";
        if (enumName == "JobSecrets") return "reusable workflow_call secrets";
        if (enumName.StartsWith("WorkflowCall", StringComparison.Ordinal)) return "workflow_call";

        // Collapse to broad levels
        if (enumName.StartsWith("Step", StringComparison.Ordinal)) return "step";
        if (enumName.StartsWith("Job", StringComparison.Ordinal)) return "job";
        return "workflow";
    }

    private static string ToPascalCase(string segment)
    {
        var parts = segment.Split(['-', '_']);
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(part[0]));
            sb.Append(part, 1, part.Length - 1);
        }
        return sb.ToString();
    }

    private static string[] Order(IReadOnlyList<string> values)
    {
        return values
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => ContextOrder.TryGetValue(x, out var order) ? order : int.MaxValue)
            .ThenBy(static x => x, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AppendArray(StringBuilder sb, string name, IReadOnlyList<string> values)
    {
        sb.AppendLine($"        static readonly byte[][] {name} =");
        sb.AppendLine("        [");
        foreach (var value in values)
        {
            sb.AppendLine($"            \"{value}\"u8.ToArray(),");
        }
        sb.AppendLine("        ];");
    }
}

using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class AvailabilityCSharpGenerator
{
    static readonly IReadOnlyDictionary<string, int> ContextOrder = new Dictionary<string, int>(StringComparer.Ordinal)
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
    };

    public string Generate(AvailabilityModel model)
    {
        var workflow = Order(model.WorkflowRoots);
        var job = Order(model.JobRoots);
        var step = Order(model.StepRoots);

        var sb = new StringBuilder();
        sb.Append(
            """
            using Seiton.Core.Parsing;

            namespace Seiton.Core.Generated;

            public static class Availability
            {
            """);

        AppendArray(sb, "WorkflowRoots", workflow);
        sb.AppendLine();
        AppendArray(sb, "JobRoots", job);
        sb.AppendLine();
        AppendArray(sb, "StepRoots", step);

        sb.Append(
            """

                public static bool IsRootContextAvailable(ExpressionValidationContext context, ReadOnlySpan<byte> rootName)
                {
                    return context switch
                    {
                        ExpressionValidationContext.Workflow => Contains(WorkflowRoots, rootName),
                        ExpressionValidationContext.Job => Contains(JobRoots, rootName),
                        ExpressionValidationContext.Step => Contains(StepRoots, rootName),
                        _ => false,
                    };
                }

                static bool Contains(byte[][] table, ReadOnlySpan<byte> name)
                {
                    for (var i = 0; i < table.Length; i++)
                    {
                        if (name.SequenceEqual(table[i]))
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }
            """);

        return TextNormalization.NormalizeToLf(sb.ToString());
    }

    static string[] Order(IReadOnlyList<string> values)
    {
        return values
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => ContextOrder.TryGetValue(x, out var order) ? order : int.MaxValue)
            .ThenBy(static x => x, StringComparer.Ordinal)
            .ToArray();
    }

    static void AppendArray(StringBuilder sb, string name, IReadOnlyList<string> values)
    {
        sb.AppendLine($"    static readonly byte[][] {name} =");
        sb.AppendLine("    [");
        foreach (var value in values)
        {
            sb.AppendLine($"        \"{value}\"u8.ToArray(),");
        }

        sb.AppendLine("    ];");
    }
}

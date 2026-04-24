using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class AvailabilityCSharpGenerator
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
    };

    public string Generate(AvailabilityModel model)
    {
        var workflow = Order(model.WorkflowRoots);
        var job = Order(model.JobRoots);
        var step = Order(model.StepRoots);
        // workflow_call outputs only allow: github, inputs, vars, jobs (no secrets)
        string[] workflowCallOutputRaw = ["github", "inputs", "vars", "jobs"];
        var workflowCallOutput = Order(workflowCallOutputRaw);
        var jobOutput = step;
        // reusable workflow call secrets: fixed contexts available in the secrets: mapping of a reusable workflow call
        string[] reusableWorkflowCallSecretsRaw = ["github", "inputs", "vars", "needs", "strategy", "matrix", "secrets"];
        var reusableWorkflowCallSecrets = Order(reusableWorkflowCallSecretsRaw);
        // strategy: contexts available in jobs.<job_id>.strategy (including matrix values)
        // Per GitHub docs: github, needs, vars, inputs — no strategy, matrix, secrets, runner, env
        string[] strategyRaw = ["github", "inputs", "vars", "needs"];
        var strategy = Order(strategyRaw);

        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-availability");
        sb.AppendLine(
            """
            using Seiton.Core.Parsing;

            namespace Seiton.Core.Generated;

            public static class Availability
            {
            """);

        AppendArray(sb, "WorkflowRoots", workflow);
        sb.AppendLine();
        AppendArray(sb, "WorkflowCallOutputRoots", workflowCallOutput);
        sb.AppendLine();
        AppendArray(sb, "JobRoots", job);
        sb.AppendLine();
        AppendArray(sb, "JobOutputRoots", jobOutput);
        sb.AppendLine();
        AppendArray(sb, "ReusableWorkflowCallSecretsRoots", reusableWorkflowCallSecrets);
        sb.AppendLine();
        AppendArray(sb, "StrategyRoots", strategy);
        sb.AppendLine();
        AppendArray(sb, "StepRoots", step);

        sb.Append(
            """

                public static bool IsRootContextAvailable(ExpressionValidationContext context, ReadOnlySpan<byte> rootName)
                {
                    return context switch
                    {
                        ExpressionValidationContext.Workflow => Contains(WorkflowRoots, rootName),
                        ExpressionValidationContext.WorkflowCallOutput => Contains(WorkflowCallOutputRoots, rootName),
                        ExpressionValidationContext.Job => Contains(JobRoots, rootName),
                        ExpressionValidationContext.JobOutput => Contains(JobOutputRoots, rootName),
                        ExpressionValidationContext.ReusableWorkflowCallSecrets => Contains(ReusableWorkflowCallSecretsRoots, rootName),
                        ExpressionValidationContext.Strategy => Contains(StrategyRoots, rootName),
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
        sb.AppendLine($"    static readonly byte[][] {name} =");
        sb.AppendLine("    [");
        foreach (var value in values)
        {
            sb.AppendLine($"        \"{value}\"u8.ToArray(),");
        }

        sb.AppendLine("    ];");
    }
}

using Seiton.Core.Parsing;

namespace Seiton.Core.Generated;

public static class Availability
{
    static readonly byte[][] WorkflowRoots =
    [
        "github"u8.ToArray(),
        "inputs"u8.ToArray(),
        "vars"u8.ToArray(),
    ];

    static readonly byte[][] JobRoots =
    [
        "github"u8.ToArray(),
        "inputs"u8.ToArray(),
        "vars"u8.ToArray(),
        "needs"u8.ToArray(),
        "strategy"u8.ToArray(),
        "matrix"u8.ToArray(),
    ];

    static readonly byte[][] StepRoots =
    [
        "github"u8.ToArray(),
        "inputs"u8.ToArray(),
        "vars"u8.ToArray(),
        "needs"u8.ToArray(),
        "strategy"u8.ToArray(),
        "matrix"u8.ToArray(),
        "job"u8.ToArray(),
        "runner"u8.ToArray(),
        "env"u8.ToArray(),
        "secrets"u8.ToArray(),
        "steps"u8.ToArray(),
    ];

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

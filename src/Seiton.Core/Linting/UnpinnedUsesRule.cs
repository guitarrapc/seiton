using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public sealed class UnpinnedUsesRule : RuleBase
{
    public override string Id => "unpinned-uses";

    public override string Name => "Unpinned Uses Rule";

    public override void VisitJobPre(Job job)
    {
        var workflowCall = job.WorkflowCall;
        if (workflowCall is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var uses = workflowCall.Uses.Value.AsSpan(Config.Utf8Yaml);
        if (ShouldSkip(uses) || IsFullLengthCommitShaPinned(uses))
        {
            return;
        }

        var jobId = Decode(job.Id.Value);
        var usesText = Decode(workflowCall.Uses.Value);
        AddJobWarning(job, $"job '{jobId}' reusable workflow uses '{usesText}' is not pinned to a full-length commit SHA");
    }

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecAction actionExec || Config.Utf8Yaml is null)
        {
            return;
        }

        var uses = actionExec.Uses.Value.AsSpan(Config.Utf8Yaml);
        if (ShouldSkip(uses) || IsFullLengthCommitShaPinned(uses))
        {
            return;
        }

        var usesText = Decode(actionExec.Uses.Value);
        AddStepWarning(step, $"action uses '{usesText}' is not pinned to a full-length commit SHA");
    }

    static bool ShouldSkip(ReadOnlySpan<byte> uses)
    {
        if (uses.IsEmpty)
        {
            return true;
        }

        return uses.StartsWith("./"u8)
            || uses.StartsWith("docker://"u8);
    }

    static bool IsFullLengthCommitShaPinned(ReadOnlySpan<byte> uses)
    {
        var at = uses.LastIndexOf((byte)'@');
        if (at < 0 || at + 1 >= uses.Length)
        {
            return false;
        }

        var reference = uses[(at + 1)..];
        if (reference.Length != 40)
        {
            return false;
        }

        for (var i = 0; i < reference.Length; i++)
        {
            var b = reference[i];
            var isDigit = b is >= (byte)'0' and <= (byte)'9';
            var isLowerHex = b is >= (byte)'a' and <= (byte)'f';
            var isUpperHex = b is >= (byte)'A' and <= (byte)'F';
            if (!isDigit && !isLowerHex && !isUpperHex)
            {
                return false;
            }
        }

        return true;
    }
}

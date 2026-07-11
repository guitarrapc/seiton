using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Linting.ActionRefHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags action references where the SHA comment tag doesn't match the actual ref version.</summary>
public sealed class RefVersionMismatchRule() : RuleBase(RuleId.RefVersionMismatch)
{
    public override string Name => "Ref Version Mismatch Rule";

    public override void VisitJobPre(JobRef job)
    {
        if (Config.Utf8Yaml is null || !job.WorkflowCall.HasValue)
        {
            return;
        }

        CheckUses(job.WorkflowCall.Uses.Value, BuildUsesLocation(job.WorkflowCall), job, default);
    }

    public override void VisitStep(StepRef step)
    {
        if (Config.Utf8Yaml is null || step.Exec.Kind != StepExecKind.Action)
        {
            return;
        }

        var action = step.Exec.AsAction();
        CheckUses(action.Uses.Value, BuildUsesLocation(action), default, step);
    }

    private void CheckUses(ReadOnlySpan<byte> uses, TextRange location, JobRef job, StepRef step)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        if (!TryParseRemoteUses(uses, out var parsed)
            || IsFullCommitSha(parsed.Ref)
            || !TryExtractRefVersionMajor(parsed.Ref, out var refMajor)
            || !TryExtractPathVersionMajor(parsed.ActionPath, out var pathMajor)
            || pathMajor == refMajor)
        {
            return;
        }

        var message = $"uses ref major version 'v{refMajor}' mismatches action path version hint 'v{pathMajor}'; align ref and path intent";
        if (step.HasValue)
        {
            AddStepWarning(step, message, location);
        }
        else if (job.HasValue)
        {
            AddJobWarning(job, message, location);
        }
    }
}

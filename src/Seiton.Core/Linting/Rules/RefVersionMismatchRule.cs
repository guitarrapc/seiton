using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Linting.ActionRefHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags action references where the SHA comment tag doesn't match the actual ref version.</summary>
public sealed class RefVersionMismatchRule() : RuleBase(RuleId.RefVersionMismatch)
{
    public override string Name => "Ref Version Mismatch Rule";

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null || job.WorkflowCall is null)
        {
            return;
        }

        CheckUses(Arena.GetStringValue(job.WorkflowCall.Uses), BuildUsesLocation(job.WorkflowCall), job, null);
    }

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecAction action)
        {
            return;
        }

        CheckUses(Arena.GetStringValue(action.Uses), BuildUsesLocation(action), null, step);
    }

    private void CheckUses(ReadOnlySpan<byte> uses, TextRange location, Job? job, Step? step)
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
        if (step is not null)
        {
            AddStepWarning(step, message, location);
        }
        else if (job is not null)
        {
            AddJobWarning(job, message, location);
        }
    }
}

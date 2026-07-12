using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Recommends trusted publishing (OIDC) over credential-based publishing for supported package registries.</summary>
public sealed class UseTrustedPublishingRule() : RuleBase(RuleId.UseTrustedPublishing)
{
    private bool workflowHasIdTokenWrite;
    private bool currentJobHasIdTokenWrite;

    public override string Name => "Use Trusted Publishing Rule";

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        workflowHasIdTokenWrite = HasIdTokenWrite(workflow.Permissions);
        currentJobHasIdTokenWrite = workflowHasIdTokenWrite;
    }

    public override void VisitJobPre(JobRef job)
    {
        currentJobHasIdTokenWrite = !job.Permissions.HasValue
            ? workflowHasIdTokenWrite
            : HasIdTokenWrite(job.Permissions);
    }

    public override void VisitStep(StepRef step)
    {
        if (Config.Utf8Yaml is null || step.Exec.Kind != StepExecKind.Run)
        {
            return;
        }

        var run = step.Exec.AsRun();
        var runText = run.Run.Value;
        if (!ContainsPublishCommand(runText) || currentJobHasIdTokenWrite)
        {
            return;
        }

        AddStepWarning(
            step,
            "publish-like command detected without id-token: write permission; use trusted publishing (OIDC) instead of long-lived registry secrets",
            run.Run.Range);
    }

    private bool HasIdTokenWrite(PermissionsRef permissions)
    {
        if (!permissions.HasValue)
        {
            return false;
        }

        if (permissions.All.HasValue)
        {
            var scalar = permissions.All.Decode();
            if (string.Equals(scalar, "write-all", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (!permissions.Scopes.HasValue || permissions.Scopes.Count == 0)
        {
            return false;
        }

        foreach (var pair in permissions.Scopes)
        {
            var key = pair.Key.Decode();
            if (!string.Equals(key, "id-token", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = pair.Value.ValueText.Decode();
            return string.Equals(value, "write", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool ContainsPublishCommand(ReadOnlySpan<byte> runText)
    {
        return ContainsAsciiIgnoreCase(runText, "npm publish"u8)
            || ContainsAsciiIgnoreCase(runText, "twine upload"u8)
            || ContainsAsciiIgnoreCase(runText, "gem push"u8)
            || ContainsAsciiIgnoreCase(runText, "poetry publish"u8);
    }
}

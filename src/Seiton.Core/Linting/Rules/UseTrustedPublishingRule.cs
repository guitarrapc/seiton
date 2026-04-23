using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Recommends trusted publishing (OIDC) over credential-based publishing for supported package registries.</summary>
public sealed class UseTrustedPublishingRule() : RuleBase(RuleId.UseTrustedPublishing)
{
    private bool workflowHasIdTokenWrite;
    private bool currentJobHasIdTokenWrite;

    public override string Name => "Use Trusted Publishing Rule";

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        workflowHasIdTokenWrite = HasIdTokenWrite(workflow.Permissions);
        currentJobHasIdTokenWrite = workflowHasIdTokenWrite;
    }

    public override void VisitJobPre(Job job)
    {
        currentJobHasIdTokenWrite = job.Permissions is null
            ? workflowHasIdTokenWrite
            : HasIdTokenWrite(job.Permissions);
    }

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecRun run)
        {
            return;
        }

        var runText = Arena.GetStringValue(run.Run);
        if (!ContainsPublishCommand(runText) || currentJobHasIdTokenWrite)
        {
            return;
        }

        AddStepWarning(
            step,
            "publish-like command detected without id-token: write permission; use trusted publishing (OIDC) instead of long-lived registry secrets",
            Arena.GetStringRange(run.Run));
    }

    private bool HasIdTokenWrite(Permissions? permissions)
    {
        if (permissions is null)
        {
            return false;
        }

        if (permissions.All.HasValue)
        {
            var scalar = Decode(Arena.GetStringSlice(permissions.All));
            if (string.Equals(scalar, "write-all", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (permissions.Scopes is null || permissions.Scopes.Value.Count == 0)
        {
            return false;
        }

        foreach (var pair in permissions.Scopes)
        {
            var key = Decode(pair.Key);
            if (!string.Equals(key, "id-token", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Decode(pair.Value.ValueText);
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

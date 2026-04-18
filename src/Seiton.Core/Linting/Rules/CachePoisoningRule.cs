using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class CachePoisoningRule : RuleBase
{
    bool hasUntrustedTrigger;
    HashSet<string>? additionalUntrustedTriggers;

    public override string Id => "cache-poisoning";

    public override string Name => "Cache Poisoning Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        additionalUntrustedTriggers = config.GetRuleConfig(Id)?.Specific is UntrustedTriggersSpecificConfig specific
            ? BuildNormalizedSet(specific.UntrustedTriggers)
            : BuildNormalizedSet(config.GetRuleConfig(Id)?.UntrustedTriggers?.Extend);
    }

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        hasUntrustedTrigger = Config.Utf8Yaml is not null && HasUntrustedTrigger(workflow, Config.Utf8Yaml, additionalUntrustedTriggers);
    }

    public override void VisitStep(Step step)
    {
        if (!hasUntrustedTrigger || step.Exec is not ExecAction action || Config.Utf8Yaml is null)
        {
            return;
        }

        var uses = action.Uses.Value.AsSpan(Config.Utf8Yaml);
        if (!IsCacheAction(uses))
        {
            return;
        }

        var actionRef = Decode(action.Uses.Value);
        AddStepWarning(
            step,
            $"cache action '{actionRef}' runs in a workflow with untrusted triggers; isolate cache scope and avoid restore-key fallback across trust boundaries",
            action.Uses.Range);
    }

    static bool HasUntrustedTrigger(Workflow workflow, byte[] utf8Yaml, HashSet<string>? additionalUntrustedTriggers)
    {
        for (var i = 0; i < workflow.On.Count; i++)
        {
            if (workflow.On[i] is not WebhookEvent webhook)
            {
                continue;
            }

            var hook = webhook.Hook.Value.AsSpan(utf8Yaml);
            if (WebhookTypes.TryGet(hook, out _, out var spec)
                && spec.Id is WebhookTypes.EventId.PullRequest
                    or WebhookTypes.EventId.PullRequestTarget
                    or WebhookTypes.EventId.WorkflowRun)
            {
                return true;
            }

            if (IsAdditionalUntrustedTrigger(hook, additionalUntrustedTriggers))
            {
                return true;
            }
        }

        return false;
    }

    static bool IsAdditionalUntrustedTrigger(ReadOnlySpan<byte> hook, HashSet<string>? additionalUntrustedTriggers)
    {
        if (additionalUntrustedTriggers is null || additionalUntrustedTriggers.Count == 0)
        {
            return false;
        }

        return additionalUntrustedTriggers.Contains(NormalizeAsciiLower(hook));
    }

    static HashSet<string>? BuildNormalizedSet(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        return new HashSet<string>(values, StringComparer.Ordinal);
    }

    static string NormalizeAsciiLower(ReadOnlySpan<byte> value)
    {
        var chars = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var b = value[i];
            chars[i] = (char)(b is >= (byte)'A' and <= (byte)'Z' ? b + 32 : b);
        }

        return new string(chars);
    }

    static bool IsCacheAction(ReadOnlySpan<byte> uses)
    {
        return IsActionReference(uses, "actions/cache"u8)
            || IsActionReference(uses, "actions/cache/restore"u8)
            || IsActionReference(uses, "actions/cache/save"u8);
    }

    static bool IsActionReference(ReadOnlySpan<byte> uses, ReadOnlySpan<byte> actionName)
    {
        var at = uses.IndexOf((byte)'@');
        if (at <= 0)
        {
            return false;
        }

        return EqualsAsciiIgnoreCase(uses[..at], actionName);
    }

    static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var l = left[i];
            var r = right[i];
            if (l is >= (byte)'A' and <= (byte)'Z')
            {
                l = (byte)(l + 32);
            }

            if (r is >= (byte)'A' and <= (byte)'Z')
            {
                r = (byte)(r + 32);
            }

            if (l != r)
            {
                return false;
            }
        }

        return true;
    }
}

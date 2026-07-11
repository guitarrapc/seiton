using System.Buffers;
using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Linting.ActionRefHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags action references matching configurable allow/deny lists.</summary>
public sealed class ForbiddenUsesRule() : RuleBase(RuleId.ForbiddenUses)
{
    private const int OwnerRepoPolicyKeyStackBytes = 512;

    private static readonly string[] DefaultDenyPatterns = ["bad-org/*"];

    private IReadOnlyList<string> allowPatterns = [];
    private IReadOnlyList<string> denyPatterns = DefaultDenyPatterns;

    public override string Name => "Forbidden Uses Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        var ruleConfig = config.GetRuleConfig(Id);
        if (ruleConfig?.Allow is not null || ruleConfig?.Deny is not null)
        {
            allowPatterns = ruleConfig.Allow ?? [];
            denyPatterns = ruleConfig.Deny is { Count: > 0 }
                ? ruleConfig.Deny
                : DefaultDenyPatterns;
            return;
        }

        allowPatterns = [];
        denyPatterns = DefaultDenyPatterns;
    }

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
        if (Config.Utf8Yaml is null || !HasPolicy())
        {
            return;
        }

        if (!TryParseRemoteUses(uses, out var parsed))
        {
            return;
        }

        if (!TryParseOwnerRepoSegments(parsed.ActionPath, out var own, out var rep))
        {
            return;
        }

        var need = own.Length + 1 + rep.Length;

        void Evaluate(ReadOnlySpan<byte> ownerRepoKey)
        {
            var matchedDeny = MatchAny(ownerRepoKey, denyPatterns);
            var matchedAllow = MatchAny(ownerRepoKey, allowPatterns);

            if (denyPatterns.Count > 0)
            {
                if (!matchedDeny || matchedAllow)
                {
                    if (matchedDeny && matchedAllow && Config.Verbose)
                    {
                        var matchedOwnerRepoText = Encoding.UTF8.GetString(ownerRepoKey);
                        var infoMessage = $"'{matchedOwnerRepoText}' matched allow pattern, skipping forbidden-uses check";
                        if (step.HasValue)
                        {
                            AddStepInfo(step, infoMessage, location);
                        }
                        else if (job.HasValue)
                        {
                            AddJobInfo(job, infoMessage, location);
                        }
                    }

                    return;
                }

                var deniedOwnerRepoText = Encoding.UTF8.GetString(ownerRepoKey);
                var message = $"uses reference '{deniedOwnerRepoText}' is denied by forbidden-uses policy";
                if (step.HasValue)
                {
                    AddStepWarning(step, message, location);
                }
                else if (job.HasValue)
                {
                    AddJobWarning(job, message, location);
                }

                return;
            }

            if (allowPatterns.Count > 0 && !matchedAllow)
            {
                var allowedOwnerRepoText = Encoding.UTF8.GetString(ownerRepoKey);
                var message = $"uses reference '{allowedOwnerRepoText}' is not in forbidden-uses allow policy";
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

        if (need <= OwnerRepoPolicyKeyStackBytes)
        {
            Span<byte> keyScratch = stackalloc byte[OwnerRepoPolicyKeyStackBytes];
            if (!TryGetOwnerRepoPolicyKey(parsed.ActionPath, keyScratch, out var ownerRepoKey))
            {
                return;
            }

            Evaluate(ownerRepoKey);
        }
        else
        {
            var rentedKey = ArrayPool<byte>.Shared.Rent(need);
            try
            {
                var span = rentedKey.AsSpan(0, need);
                if (!TryGetOwnerRepoPolicyKey(parsed.ActionPath, span, out var ownerRepoKey))
                {
                    return;
                }

                Evaluate(ownerRepoKey);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedKey);
            }
        }
    }

    private bool HasPolicy()
    {
        return allowPatterns.Count > 0 || denyPatterns.Count > 0;
    }

    private static bool MatchAny(ReadOnlySpan<byte> ownerRepoKeyUtf8, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return false;
        }

        Span<byte> patternScratch = stackalloc byte[512];
        for (var i = 0; i < patterns.Count; i++)
        {
            var pat = patterns[i];
            if (Encoding.UTF8.TryGetBytes(pat, patternScratch, out var patLen))
            {
                if (WildcardMatchUsesPolicy(ownerRepoKeyUtf8, patternScratch[..patLen]))
                {
                    return true;
                }
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(pat);
                if (WildcardMatchUsesPolicy(ownerRepoKeyUtf8, bytes))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

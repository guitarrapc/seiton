using System.Buffers;
using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Linting.ActionRefHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class ForbiddenUsesRule : RuleBase
{
    private const int OwnerRepoPolicyKeyStackBytes = 512;

    private static readonly string[] DefaultDenyPatterns = ["bad-org/*"];

    private IReadOnlyList<string> allowPatterns = [];
    private IReadOnlyList<string> denyPatterns = DefaultDenyPatterns;

    public override string Id => "forbidden-uses";

    public override string Name => "Forbidden Uses Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        var ruleConfig = config.GetRuleConfig(Id);
        if (ruleConfig?.Specific is ForbiddenUsesSpecificConfig specific)
        {
            allowPatterns = specific.Allow ?? [];
            denyPatterns = specific.Deny is { Count: > 0 }
                ? specific.Deny
                : DefaultDenyPatterns;
            return;
        }

        allowPatterns = [];
        denyPatterns = DefaultDenyPatterns;
    }

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
                    return;
                }

                var message = $"uses reference '{Encoding.UTF8.GetString(ownerRepoKey)}' is denied by forbidden-uses policy";
                if (step is not null)
                {
                    AddStepWarning(step, message, location);
                }
                else if (job is not null)
                {
                    AddJobWarning(job, message, location);
                }

                return;
            }

            if (allowPatterns.Count > 0 && !matchedAllow)
            {
                var message = $"uses reference '{Encoding.UTF8.GetString(ownerRepoKey)}' is not in forbidden-uses allow policy";
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

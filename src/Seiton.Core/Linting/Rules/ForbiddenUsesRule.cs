using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class ForbiddenUsesRule : RuleBase
{
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

        CheckUses(job.WorkflowCall.Uses, BuildUsesLocation(job.WorkflowCall), job, null);
    }

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecAction action)
        {
            return;
        }

        CheckUses(action.Uses, BuildUsesLocation(action), null, step);
    }

    private void CheckUses(StringNode usesNode, TextRange location, Job? job, Step? step)
    {
        if (Config.Utf8Yaml is null || !HasPolicy())
        {
            return;
        }

        if (!TryGetOwnerRepo(usesNode.Value.AsSpan(Config.Utf8Yaml), out var ownerRepo))
        {
            return;
        }

        var matchedDeny = MatchAny(ownerRepo, denyPatterns);
        var matchedAllow = MatchAny(ownerRepo, allowPatterns);

        if (denyPatterns.Count > 0)
        {
            if (!matchedDeny || matchedAllow)
            {
                return;
            }

            var message = $"uses reference '{ownerRepo}' is denied by forbidden-uses policy";
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
            var message = $"uses reference '{ownerRepo}' is not in forbidden-uses allow policy";
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

    private bool HasPolicy()
    {
        return allowPatterns.Count > 0 || denyPatterns.Count > 0;
    }

    private static bool MatchAny(string ownerRepo, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < patterns.Count; i++)
        {
            if (WildcardMatch(ownerRepo, patterns[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WildcardMatch(string text, string pattern)
    {
        var textIndex = 0;
        var patternIndex = 0;
        var starIndex = -1;
        var matchIndex = 0;

        while (textIndex < text.Length)
        {
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == '?' || pattern[patternIndex] == text[textIndex]))
            {
                patternIndex++;
                textIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex;
                matchIndex = textIndex;
                patternIndex++;
                continue;
            }

            if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                matchIndex++;
                textIndex = matchIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    private static bool TryGetOwnerRepo(ReadOnlySpan<byte> uses, out string ownerRepo)
    {
        ownerRepo = string.Empty;
        if (uses.IsEmpty || uses.StartsWith("./"u8) || uses.StartsWith("docker://"u8))
        {
            return false;
        }

        var at = uses.LastIndexOf((byte)'@');
        if (at <= 0 || at + 1 >= uses.Length)
        {
            return false;
        }

        var path = uses[..at];
        var slash1 = path.IndexOf((byte)'/');
        if (slash1 <= 0 || slash1 + 1 >= path.Length)
        {
            return false;
        }

        var rest = path[(slash1 + 1)..];
        var slash2 = rest.IndexOf((byte)'/');
        if (slash2 == 0)
        {
            return false;
        }

        var owner = path[..slash1];
        var repo = slash2 < 0 ? rest : rest[..slash2];
        if (owner.Length == 0 || repo.Length == 0)
        {
            return false;
        }

        ownerRepo = string.Concat(NormalizeAsciiLower(owner), "/", NormalizeAsciiLower(repo));
        return true;
    }
}

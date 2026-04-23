using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class ArchivedUsesRule() : RuleBase(RuleId.ArchivedUses)
{
    private static readonly HashSet<string> ArchivedRepositories = new(StringComparer.Ordinal)
    {
        "actions-rs/toolchain",
        "actions-rs/cargo",
        "actions-rs/audit-check",
        "actions-rs/clippy-check",
    };

    public override string Name => "Archived Uses Rule";

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null || job.WorkflowCall is null)
        {
            return;
        }

        if (!TryGetOwnerRepo(Arena.GetStringValue(job.WorkflowCall.Uses), out var ownerRepo))
        {
            return;
        }

        if (!ArchivedRepositories.Contains(ownerRepo))
        {
            return;
        }

        AddJobWarning(
            job,
            $"reusable workflow uses archived repository '{ownerRepo}'; replace with actively maintained alternative",
            BuildUsesLocation(job.WorkflowCall));
    }

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecAction action)
        {
            return;
        }

        if (!TryGetOwnerRepo(Arena.GetStringValue(action.Uses), out var ownerRepo))
        {
            return;
        }

        if (!ArchivedRepositories.Contains(ownerRepo))
        {
            return;
        }

        AddStepWarning(
            step,
            $"action uses archived repository '{ownerRepo}'; replace with actively maintained alternative",
            BuildUsesLocation(action));
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

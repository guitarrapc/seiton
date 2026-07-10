using System.Diagnostics.CodeAnalysis;
using System.Text;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags action references to archived (read-only) GitHub repositories.</summary>
public sealed class ArchivedUsesRule() : RuleBase(RuleId.ArchivedUses)
{
    // UTF-8 owner/repo plus its prebuilt diagnostic message: matching compares spans and
    // reuses the static message, so both the clean path and the diagnostic path stay
    // allocation-free (this check runs for every action step and workflow-call job).
    private static readonly (byte[] OwnerRepoUtf8, string Message)[] ArchivedRepositories =
    [
        Entry("actions-rs/toolchain"),
        Entry("actions-rs/cargo"),
        Entry("actions-rs/audit-check"),
        Entry("actions-rs/clippy-check"),
    ];

    private static (byte[] OwnerRepoUtf8, string Message) Entry(string ownerRepo) =>
        (Encoding.UTF8.GetBytes(ownerRepo), $"'{ownerRepo}' is archived; replace with actively maintained alternative");

    public override string Name => "Archived Uses Rule";

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null || job.WorkflowCall is null)
        {
            return;
        }

        if (!TryGetOwnerRepo(Arena.GetStringValue(job.WorkflowCall.Uses), out var owner, out var repo))
        {
            return;
        }

        if (!TryGetArchivedMessage(owner, repo, out var message))
        {
            return;
        }

        AddJobWarning(job, message, BuildUsesLocation(job.WorkflowCall));
    }

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecAction action)
        {
            return;
        }

        if (!TryGetOwnerRepo(Arena.GetStringValue(action.Uses), out var owner, out var repo))
        {
            return;
        }

        if (!TryGetArchivedMessage(owner, repo, out var message))
        {
            return;
        }

        AddStepWarning(step, message, BuildUsesLocation(action));
    }

    private static bool TryGetArchivedMessage(ReadOnlySpan<byte> owner, ReadOnlySpan<byte> repo, [NotNullWhen(true)] out string? message)
    {
        foreach (var (ownerRepoUtf8, archivedMessage) in ArchivedRepositories)
        {
            var candidate = ownerRepoUtf8.AsSpan();
            if (candidate.Length != owner.Length + repo.Length + 1
                || candidate[owner.Length] != (byte)'/'
                || !EqualsAsciiIgnoreCase(candidate[..owner.Length], owner)
                || !EqualsAsciiIgnoreCase(candidate[(owner.Length + 1)..], repo))
            {
                continue;
            }

            message = archivedMessage;
            return true;
        }

        message = null;
        return false;
    }

    private static bool TryGetOwnerRepo(ReadOnlySpan<byte> uses, out ReadOnlySpan<byte> owner, out ReadOnlySpan<byte> repo)
    {
        owner = default;
        repo = default;
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

        owner = path[..slash1];
        repo = slash2 < 0 ? rest : rest[..slash2];
        return owner.Length > 0 && repo.Length > 0;
    }
}

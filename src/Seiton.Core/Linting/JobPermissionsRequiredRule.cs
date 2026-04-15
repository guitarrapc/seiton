using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public sealed class JobPermissionsRequiredRule : RuleBase
{
    public override string Id => "job-permissions-required";

    public override string Name => "Job Permissions Required Rule";

    public override void VisitJobPre(Job job)
    {
        if (job.Permissions is not null)
        {
            return;
        }

        var jobId = Decode(job.Id.Value);
        AddJobWarning(job, $"job '{jobId}' does not have permissions defined; set explicit permissions to follow least-privilege principle");
    }
}

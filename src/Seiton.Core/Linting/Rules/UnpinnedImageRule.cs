using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags container images not pinned to a digest (<c>@sha256:...</c>).</summary>
public sealed class UnpinnedImageRule() : RuleBase(RuleId.UnpinnedImage)
{
    public override string Name => "Unpinned Image Rule";

    public override void VisitJobPre(JobRef job)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        ReportIfUnpinnedContainerImage(job, job.Container.Image, "job.container");

        var serviceMap = job.Services.ServiceMap;
        if (serviceMap.Count == 0)
        {
            return;
        }

        foreach (var pair in serviceMap)
        {
            var service = pair.Value;
            ReportIfUnpinnedContainerImage(job, service.Container.Image, "job.services");
        }
    }

    public override void VisitStep(StepRef step)
    {
        if (step.Exec.Kind != StepExecKind.Action || Config.Utf8Yaml is null)
        {
            return;
        }

        var actionExec = step.Exec.AsAction();
        var usesNode = actionExec.Uses;
        if (usesNode.Expression.HasValue)
        {
            return;
        }

        var uses = usesNode.Value;
        if (!uses.StartsWith("docker://"u8))
        {
            return;
        }

        var image = uses["docker://"u8.Length..];
        if (ActionRefHelpers.IsSha256DigestPinned(image))
        {
            return;
        }

        var usesText = usesNode.Decode();
        var usesLocation = actionExec.UsesKeyRange ?? usesNode.Range;
        AddStepWarning(step, $"'{usesText}' is not pinned by digest (expected @sha256:<64-hex>)", usesLocation, PinDiagnosticMetadata.ForImageRef(usesText));
    }

    private void ReportIfUnpinnedContainerImage(JobRef job, StringRef imageNode, string locationName)
    {
        if (!imageNode.HasValue || imageNode.Expression.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var image = imageNode.Value;
        if (image.IsEmpty || ActionRefHelpers.IsSha256DigestPinned(image))
        {
            return;
        }

        var imageText = imageNode.Decode();
        AddJobWarning(job, $"{locationName} image '{imageText}' is not pinned by digest (expected @sha256:<64-hex>)", imageNode.Range, PinDiagnosticMetadata.ForImageRef(imageText));
    }
}

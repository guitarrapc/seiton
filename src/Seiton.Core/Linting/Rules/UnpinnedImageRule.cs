using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class UnpinnedImageRule : RuleBase
{
    public override string Id => "unpinned-image";

    public override string Name => "Unpinned Image Rule";

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        ReportIfUnpinnedContainerImage(job, job.Container is not null ? job.Container.Image : default, "job.container");

        var serviceMap = job.Services?.ServiceMap;
        if (serviceMap is null || serviceMap.Value.Count == 0)
        {
            return;
        }

        foreach (var pair in serviceMap.Value)
        {
            var service = pair.Value;
            ReportIfUnpinnedContainerImage(job, service.Container.Image, "job.services");
        }
    }

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecAction actionExec || Config.Utf8Yaml is null)
        {
            return;
        }

        var usesNode = actionExec.Uses;
        if (Arena.GetStringExpression(usesNode).HasValue)
        {
            return;
        }

        var uses = Arena.GetStringValue(usesNode);
        if (!uses.StartsWith("docker://"u8))
        {
            return;
        }

        var image = uses["docker://"u8.Length..];
        if (ActionRefHelpers.IsSha256DigestPinned(image))
        {
            return;
        }

        var usesText = Decode(Arena.GetStringSlice(usesNode));
        var usesLocation = actionExec.UsesKeyRange ?? Arena.GetStringRange(usesNode);
        AddStepWarning(step, $"docker action uses '{usesText}' is not pinned by digest (expected @sha256:<64-hex>)", usesLocation, PinDiagnosticMetadata.ForImageRef(usesText));
    }

    private void ReportIfUnpinnedContainerImage(Job job, StringNodeId imageNode, string locationName)
    {
        if (!imageNode.HasValue || Arena.GetStringExpression(imageNode).HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var image = Arena.GetStringValue(imageNode);
        if (image.IsEmpty || ActionRefHelpers.IsSha256DigestPinned(image))
        {
            return;
        }

        var imageText = Decode(Arena.GetStringSlice(imageNode));
        AddJobWarning(job, $"{locationName} image '{imageText}' is not pinned by digest (expected @sha256:<64-hex>)", Arena.GetStringRange(imageNode), PinDiagnosticMetadata.ForImageRef(imageText));
    }
}

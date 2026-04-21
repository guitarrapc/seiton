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

        ReportIfUnpinnedContainerImage(job, job.Container?.Image, "job.container");

        var serviceMap = job.Services?.ServiceMap;
        if (serviceMap is null || serviceMap.Count == 0)
        {
            return;
        }

        foreach (var pair in serviceMap)
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
        if (usesNode.Expression is not null)
        {
            return;
        }

        var uses = usesNode.Value.AsSpan(Config.Utf8Yaml);
        if (!uses.StartsWith("docker://"u8))
        {
            return;
        }

        var image = uses["docker://"u8.Length..];
        if (IsSha256DigestPinned(image))
        {
            return;
        }

        var usesText = Decode(usesNode.Value);
        AddStepWarning(step, $"docker action uses '{usesText}' is not pinned by digest (expected @sha256:<64-hex>)");
    }

    private void ReportIfUnpinnedContainerImage(Job job, StringNode? imageNode, string locationName)
    {
        if (imageNode is null || imageNode.Expression is not null || Config.Utf8Yaml is null)
        {
            return;
        }

        var image = imageNode.Value.AsSpan(Config.Utf8Yaml);
        if (image.IsEmpty || IsSha256DigestPinned(image))
        {
            return;
        }

        var imageText = Decode(imageNode.Value);
        AddJobWarning(job, $"{locationName} image '{imageText}' is not pinned by digest (expected @sha256:<64-hex>)", imageNode.Range);
    }
}

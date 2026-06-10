using Seiton.Core.Parsing;

using static Seiton.Core.Linting.ActionRefHelpers;

namespace Seiton.Core.Linting.PinRemediation;

/// <summary>
/// Orchestrates network-based pinning remediation: resolves action SHAs and image digests,
/// then attaches auto-fix edits to unpinned-uses/unpinned-image diagnostics.
/// </summary>
public sealed class PinRemediationEngine(
    IActionShaResolver? actionShaResolver,
    IImageDigestResolver? imageDigestResolver,
    FixPinningConfig pinningConfig,
    FixImagesConfig imagesConfig,
    NetworkConfig networkConfig)
{
    private const string UsesRuleId = "unpinned-uses";
    private const string ImageRuleId = "unpinned-image";

    private readonly IActionShaResolver? _actionShaResolver = actionShaResolver;
    private readonly IImageDigestResolver? _imageDigestResolver = imageDigestResolver;
    private readonly FixPinningConfig _pinningConfig = pinningConfig ?? new FixPinningConfig();
    private readonly FixImagesConfig _imagesConfig = imagesConfig ?? new FixImagesConfig();
    private readonly NetworkConfig _networkConfig = networkConfig ?? new NetworkConfig();

    /// <summary>Resolves unpinned action and image references in the given diagnostics, producing fixes where possible.</summary>
    public async Task<RemediationResult> RemediateAsync(
        IReadOnlyList<Diagnostic> diagnostics,
        byte[] utf8Yaml,
        CancellationToken cancellationToken = default)
    {
        if (diagnostics.Count == 0)
        {
            return new RemediationResult(diagnostics, ResolvedCount: 0, SkippedCount: 0, FailedCount: 0);
        }

        var hasNetwork = _pinningConfig.EnableNetwork || _imagesConfig.EnableNetwork;
        if (!hasNetwork || (_actionShaResolver is null && _imageDigestResolver is null))
        {
            return new RemediationResult(diagnostics, ResolvedCount: 0, SkippedCount: 0, FailedCount: 0);
        }

        var outputs = new Diagnostic[diagnostics.Count];
        var resolvedCount = 0;
        var skippedCount = 0;
        var failedCount = 0;
        var maxConcurrency = Math.Max(1, _networkConfig.MaxConcurrency);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var tasks = new Task[diagnostics.Count];
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var index = i;
            tasks[index] = Task.Run(async () =>
            {
                var diagnostic = diagnostics[index];
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var output = await RemediateOneAsync(diagnostic, utf8Yaml, cancellationToken);
                    outputs[index] = output.Diagnostic;
                    switch (output.Status)
                    {
                        case RemediationStatus.Resolved:
                            Interlocked.Increment(ref resolvedCount);
                            break;
                        case RemediationStatus.Skipped:
                            Interlocked.Increment(ref skippedCount);
                            break;
                        case RemediationStatus.Failed:
                            Interlocked.Increment(ref failedCount);
                            break;
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken);
        }

        await Task.WhenAll(tasks);
        return new RemediationResult(outputs, resolvedCount, skippedCount, failedCount);
    }

    private async Task<RemediationOutcome> RemediateOneAsync(
        Diagnostic diagnostic,
        byte[] utf8Yaml,
        CancellationToken cancellationToken)
    {
        if (diagnostic.RuleId is not (UsesRuleId or ImageRuleId))
        {
            return new RemediationOutcome(diagnostic, RemediationStatus.None);
        }

        var timeout = _networkConfig.TimeoutSeconds > 0
            ? TimeSpan.FromSeconds(_networkConfig.TimeoutSeconds)
            : Timeout.InfiniteTimeSpan;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            cts.CancelAfter(timeout);
        }

        try
        {
            if (diagnostic.RuleId == UsesRuleId)
            {
                return await RemediateUnpinnedUsesAsync(diagnostic, utf8Yaml, cts.Token);
            }

            return await RemediateUnpinnedImageAsync(diagnostic, utf8Yaml, cts.Token);
        }
        catch when (_networkConfig.OnError == NetworkErrorMode.Skip)
        {
            return new RemediationOutcome(diagnostic, RemediationStatus.Failed);
        }
    }

    private async Task<RemediationOutcome> RemediateUnpinnedUsesAsync(
        Diagnostic diagnostic,
        byte[] utf8Yaml,
        CancellationToken cancellationToken)
    {
        if (_actionShaResolver is null)
        {
            return new RemediationOutcome(diagnostic, RemediationStatus.Skipped);
        }

        if (!PinDiagnosticMetadata.TryGetUsesRef(diagnostic, out var usesRef)
            || !TryParseActionReference(usesRef, out var owner, out var repo, out var currentRef))
        {
            return new RemediationOutcome(diagnostic, RemediationStatus.Failed);
        }

        var resolution = await _actionShaResolver.ResolveAsync(owner, repo, currentRef, cancellationToken);
        if (resolution.Sha is null || resolution.TagComment is null)
        {
            if (!string.IsNullOrWhiteSpace(resolution.SkipReason))
            {
                var help = PinRemediationTextHelpers.AppendHelp(diagnostic.Help, resolution.SkipReason);
                return new RemediationOutcome(diagnostic with { Help = help }, RemediationStatus.Skipped);
            }

            return new RemediationOutcome(diagnostic, RemediationStatus.Skipped);
        }

        var fix = PinFixFormatter.BuildActionsShaFix(diagnostic, resolution.Sha, resolution.TagComment, utf8Yaml);
        if (fix is null)
        {
            return new RemediationOutcome(diagnostic, RemediationStatus.Failed);
        }

        return new RemediationOutcome(diagnostic with { Fix = fix.Value }, RemediationStatus.Resolved);
    }

    private async Task<RemediationOutcome> RemediateUnpinnedImageAsync(
        Diagnostic diagnostic,
        byte[] utf8Yaml,
        CancellationToken cancellationToken)
    {
        if (_imageDigestResolver is null)
        {
            return new RemediationOutcome(diagnostic, RemediationStatus.Skipped);
        }

        if (!PinDiagnosticMetadata.TryGetImageRef(diagnostic, out var imageRef))
        {
            return new RemediationOutcome(diagnostic, RemediationStatus.Failed);
        }

        var resolution = await _imageDigestResolver.ResolveAsync(imageRef, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolution.SkipReason))
        {
            var help = PinRemediationTextHelpers.AppendHelp(diagnostic.Help, resolution.SkipReason);
            return new RemediationOutcome(diagnostic with { Help = help }, RemediationStatus.Skipped);
        }

        if (resolution.Digest is null)
        {
            return new RemediationOutcome(diagnostic, RemediationStatus.Skipped);
        }

        var fix = PinFixFormatter.BuildImageDigestFix(diagnostic, resolution.Digest, utf8Yaml);
        if (fix is null)
        {
            return new RemediationOutcome(diagnostic, RemediationStatus.Failed);
        }

        return new RemediationOutcome(diagnostic with { Fix = fix.Value }, RemediationStatus.Resolved);
    }

    private enum RemediationStatus
    {
        None,
        Resolved,
        Skipped,
        Failed,
    }

    private readonly record struct RemediationOutcome(Diagnostic Diagnostic, RemediationStatus Status);

}

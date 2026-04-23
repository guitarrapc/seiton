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
                    if (output.Resolved)
                    {
                        Interlocked.Increment(ref resolvedCount);
                    }
                    if (output.Skipped)
                    {
                        Interlocked.Increment(ref skippedCount);
                    }
                    if (output.Failed)
                    {
                        Interlocked.Increment(ref failedCount);
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
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: false, Failed: false);
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
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: false, Failed: true);
        }
    }

    private async Task<RemediationOutcome> RemediateUnpinnedUsesAsync(
        Diagnostic diagnostic,
        byte[] utf8Yaml,
        CancellationToken cancellationToken)
    {
        if (_actionShaResolver is null)
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: true, Failed: false);
        }

        if (!PinDiagnosticMetadata.TryGetUsesRef(diagnostic, out var usesRef)
            || !TryParseActionReference(usesRef, out var owner, out var repo, out var currentRef))
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: false, Failed: true);
        }

        var (sha, tagComment) = await _actionShaResolver.ResolveAsync(owner, repo, currentRef, cancellationToken);
        if (sha is null || tagComment is null)
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: true, Failed: false);
        }

        var fix = PinFixFormatter.BuildActionsShaFix(diagnostic, sha, tagComment, utf8Yaml);
        if (fix is null)
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: false, Failed: true);
        }

        return new RemediationOutcome(diagnostic with { Fix = fix.Value }, Resolved: true, Skipped: false, Failed: false);
    }

    private async Task<RemediationOutcome> RemediateUnpinnedImageAsync(
        Diagnostic diagnostic,
        byte[] utf8Yaml,
        CancellationToken cancellationToken)
    {
        if (_imageDigestResolver is null)
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: true, Failed: false);
        }

        if (!PinDiagnosticMetadata.TryGetImageRef(diagnostic, out var imageRef))
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: false, Failed: true);
        }

        var digest = await _imageDigestResolver.ResolveAsync(imageRef, cancellationToken);
        if (digest is null)
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: true, Failed: false);
        }

        var fix = PinFixFormatter.BuildImageDigestFix(diagnostic, digest, utf8Yaml);
        if (fix is null)
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: false, Failed: true);
        }

        return new RemediationOutcome(diagnostic with { Fix = fix.Value }, Resolved: true, Skipped: false, Failed: false);
    }

    private readonly record struct RemediationOutcome(Diagnostic Diagnostic, bool Resolved, bool Skipped, bool Failed);
}

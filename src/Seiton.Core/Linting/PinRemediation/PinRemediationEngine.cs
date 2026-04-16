using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting.PinRemediation;

public sealed class PinRemediationEngine(
    IActionShaResolver? actionShaResolver,
    IImageDigestResolver? imageDigestResolver,
    PinResolutionConfig config)
{
    const string UsesRuleId = "unpinned-uses";
    const string ImageRuleId = "unpinned-image";

    readonly IActionShaResolver? _actionShaResolver = actionShaResolver;
    readonly IImageDigestResolver? _imageDigestResolver = imageDigestResolver;
    readonly PinResolutionConfig _config = config ?? PinResolutionConfig.Default;

    public async Task<RemediationResult> RemediateAsync(
        IReadOnlyList<Diagnostic> diagnostics,
        byte[] utf8Yaml,
        CancellationToken cancellationToken = default)
    {
        if (diagnostics.Count == 0)
        {
            return new RemediationResult(diagnostics, ResolvedCount: 0, SkippedCount: 0, FailedCount: 0);
        }

        if (!_config.AllowNetwork || (_actionShaResolver is null && _imageDigestResolver is null))
        {
            return new RemediationResult(diagnostics, ResolvedCount: 0, SkippedCount: 0, FailedCount: 0);
        }

        var outputs = new Diagnostic[diagnostics.Count];
        var resolvedCount = 0;
        var skippedCount = 0;
        var failedCount = 0;
        var maxConcurrency = Math.Max(1, _config.MaxConcurrency);
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

    async Task<RemediationOutcome> RemediateOneAsync(
        Diagnostic diagnostic,
        byte[] utf8Yaml,
        CancellationToken cancellationToken)
    {
        if (diagnostic.RuleId is not (UsesRuleId or ImageRuleId))
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: false, Failed: false);
        }

        var timeout = _config.RequestTimeoutSec > 0
            ? TimeSpan.FromSeconds(_config.RequestTimeoutSec)
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
        catch when (_config.FailOpen)
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: false, Failed: true);
        }
    }

    async Task<RemediationOutcome> RemediateUnpinnedUsesAsync(
        Diagnostic diagnostic,
        byte[] utf8Yaml,
        CancellationToken cancellationToken)
    {
        if (_actionShaResolver is null)
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: true, Failed: false);
        }

        if (!TryExtractQuotedValue(diagnostic.Message, out var usesRef)
            || !TryParseActionReference(usesRef, out var owner, out var repo, out var currentRef))
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: false, Failed: true);
        }

        var (sha, tagComment) = await _actionShaResolver.ResolveAsync(owner, repo, currentRef, cancellationToken);
        if (sha is null || tagComment is null)
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: true, Failed: false);
        }

        var at = usesRef.LastIndexOf('@');
        if (at < 0)
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: false, Failed: true);
        }

        var replacement = usesRef[..(at + 1)] + sha + " # " + tagComment;
        if (!TryBuildReplacementFix(diagnostic, usesRef, replacement, utf8Yaml, "Pin action reference to resolved SHA", out var fix))
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: false, Failed: true);
        }

        return new RemediationOutcome(diagnostic with { Fix = fix }, Resolved: true, Skipped: false, Failed: false);
    }

    async Task<RemediationOutcome> RemediateUnpinnedImageAsync(
        Diagnostic diagnostic,
        byte[] utf8Yaml,
        CancellationToken cancellationToken)
    {
        if (_imageDigestResolver is null)
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: true, Failed: false);
        }

        if (!TryExtractQuotedValue(diagnostic.Message, out var imageRef))
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: false, Failed: true);
        }

        var digest = await _imageDigestResolver.ResolveAsync(imageRef, cancellationToken);
        if (digest is null)
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: true, Failed: false);
        }

        var replacement = imageRef.Contains("@sha256:", StringComparison.OrdinalIgnoreCase)
            ? imageRef
            : $"{imageRef}@{digest}";

        if (!TryBuildReplacementFix(diagnostic, imageRef, replacement, utf8Yaml, "Pin image reference to resolved digest", out var fix))
        {
            return new RemediationOutcome(diagnostic, Resolved: false, Skipped: false, Failed: true);
        }

        return new RemediationOutcome(diagnostic with { Fix = fix }, Resolved: true, Skipped: false, Failed: false);
    }

    static bool TryExtractQuotedValue(string message, out string value)
    {
        var first = message.IndexOf('\'');
        if (first < 0)
        {
            value = string.Empty;
            return false;
        }

        var second = message.IndexOf('\'', first + 1);
        if (second <= first)
        {
            value = string.Empty;
            return false;
        }

        value = message[(first + 1)..second];
        return !string.IsNullOrEmpty(value);
    }

    static bool TryParseActionReference(string usesRef, out string owner, out string repo, out string reference)
    {
        owner = string.Empty;
        repo = string.Empty;
        reference = string.Empty;

        var at = usesRef.LastIndexOf('@');
        if (at <= 0 || at == usesRef.Length - 1)
        {
            return false;
        }

        var actionPath = usesRef[..at];
        reference = usesRef[(at + 1)..];

        var slash1 = actionPath.IndexOf('/');
        if (slash1 <= 0 || slash1 == actionPath.Length - 1)
        {
            return false;
        }

        var slash2 = actionPath.IndexOf('/', slash1 + 1);
        owner = actionPath[..slash1];
        repo = slash2 < 0 ? actionPath[(slash1 + 1)..] : actionPath.Substring(slash1 + 1, slash2 - (slash1 + 1));

        return owner.Length > 0 && repo.Length > 0 && reference.Length > 0;
    }

    static bool TryBuildReplacementFix(
        Diagnostic diagnostic,
        string oldValue,
        string newValue,
        byte[] utf8Yaml,
        string description,
        out DiagnosticFix fix)
    {
        var oldBytes = Encoding.UTF8.GetBytes(oldValue);

        var rangeStart = Math.Max(0, diagnostic.Location.Start);
        var rangeLength = Math.Max(0, diagnostic.Location.Length);
        var rangeEnd = Math.Min(utf8Yaml.Length, rangeStart + rangeLength);

        if (rangeStart <= rangeEnd)
        {
            var segment = utf8Yaml.AsSpan(rangeStart, rangeEnd - rangeStart);
            var local = segment.IndexOf(oldBytes);
            if (local >= 0)
            {
                var offset = rangeStart + local;
                fix = new DiagnosticFix(description, [new TextEdit(offset, oldBytes.Length, newValue)]);
                return true;
            }
        }

        // Fallback to file-wide search when diagnostic range is broader than the target scalar.
        var global = utf8Yaml.AsSpan().IndexOf(oldBytes);
        if (global >= 0)
        {
            fix = new DiagnosticFix(description, [new TextEdit(global, oldBytes.Length, newValue)]);
            return true;
        }

        fix = default;
        return false;
    }

    readonly record struct RemediationOutcome(Diagnostic Diagnostic, bool Resolved, bool Skipped, bool Failed);
}

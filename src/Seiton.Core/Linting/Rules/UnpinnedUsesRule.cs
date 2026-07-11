using System.Buffers;
using System.Text;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Linting.ActionRefHelpers;
using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags action references not pinned to a full commit SHA.</summary>
public sealed class UnpinnedUsesRule() : RuleBase(RuleId.UnpinnedUses)
{
    // Cache last-produced "not pinned" message and decoded text to avoid repeated string allocation
    // for the same action ref (common: all steps use the same action)
    private Utf8Slice _lastUnpinnedStepUsesSlice;
    private string? _lastUnpinnedStepMessage;
    private string? _lastDecodedUsesText;

    private IgnoreEntry[] _ignoreEntries = [];

    // Track owners for which we have already emitted a help hint (once per owner per workflow).
    // Two-level cache: fast byte-span check for the last owner (hot path), HashSet for multi-owner (rare).
    private readonly HashSet<string> _hintedOwners = new(StringComparer.OrdinalIgnoreCase);
    private byte[]? _lastHintedOwnerBytes;

    public override string Name => "Unpinned Uses Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        var ruleConfig = config.GetRuleConfig(Id);
        var ignoreActions = ruleConfig?.IgnoreActions;
        if (ignoreActions is { Count: > 0 })
        {
            _ignoreEntries = new IgnoreEntry[ignoreActions.Count];
            for (var i = 0; i < ignoreActions.Count; i++)
            {
                var rule = ignoreActions[i];
                var patternBytes = Encoding.UTF8.GetBytes(NormalizeAsciiLower(rule.Pattern));
                byte[][]? refsBytes = null;
                if (rule.Refs is { Count: > 0 })
                {
                    refsBytes = new byte[rule.Refs.Count][];
                    for (var j = 0; j < rule.Refs.Count; j++)
                    {
                        refsBytes[j] = Encoding.UTF8.GetBytes(rule.Refs[j]);
                    }
                }

                _ignoreEntries[i] = new IgnoreEntry(patternBytes, refsBytes);
            }
        }
        else
        {
            _ignoreEntries = [];
        }
    }

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        // Clear per-source cache — slice offsets are invalid across different source bytes.
        _lastUnpinnedStepUsesSlice = default;
        _lastUnpinnedStepMessage = null;
        _lastDecodedUsesText = null;
        _hintedOwners.Clear();
        _lastHintedOwnerBytes = null;
    }

    public override void VisitJobPre(JobRef job)
    {
        var workflowCall = job.WorkflowCall;
        if (!workflowCall.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var uses = workflowCall.Uses.Value;
        var usesLocation = BuildUsesLocation(workflowCall);
        var usesRefLocation = BuildRefLocation(workflowCall.Uses.Slice, uses, Config.Utf8Yaml, usesLocation);
        if (uses.StartsWith("./"u8))
        {
            if (uses.IndexOf((byte)'@') >= 0)
            {
                var localJobId = job.Id.Decode();
                AddJobWarning(
                    job,
                    $"jobs.'{localJobId}'.uses local reusable workflow reference must not contain '@ref'",
                    usesRefLocation);
            }

            return;
        }

        // ../ prefix is not valid for reusable workflows (only ./ is allowed).
        // ReusableWorkflowRule owns this diagnostic; silently return to avoid double-reporting.
        if (uses.StartsWith("../"u8))
        {
            return;
        }

        if (!TryParseRemoteUses(uses, out var parsedJob))
        {
            var formatJobId = job.Id.Decode();
            var invalidUsesText = workflowCall.Uses.Decode();
            AddJobError(
                job,
                $"jobs.'{formatJobId}'.uses '{invalidUsesText}' has invalid reference format; expected owner/repo/path@ref",
                usesLocation);
            return;
        }

        if (IsFullCommitSha(parsedJob.Ref))
        {
            return;
        }

        if (IsIgnoredAction(parsedJob.ActionPath, parsedJob.Ref))
        {
            if (Config.Verbose)
            {
                var ignoredUsesText = workflowCall.Uses.Decode();
                AddJobInfo(job, $"ignored '{ignoredUsesText}' (matched ignore-actions pattern)", usesLocation);
            }

            return;
        }

        var jobId = job.Id.Decode();
        var usesText = workflowCall.Uses.Decode();
        var url = ActionRefHelpers.BuildGitHubUrl(usesText);
        var urlSuffix = url is not null ? $". see {url}" : "";
        var help = BuildOwnerHintOnce(parsedJob.ActionPath);
        AddJobWarning(job, $"jobs.'{jobId}'.uses '{usesText}' is not pinned to a full-length commit SHA{urlSuffix} (fixable with --fix --enable-pin-network)", usesRefLocation, PinDiagnosticMetadata.ForUsesRef(usesText), help);
    }

    public override void VisitStep(StepRef step)
    {
        if (step.Exec.Kind != StepExecKind.Action || Config.Utf8Yaml is null)
        {
            return;
        }

        var actionExec = step.Exec.AsAction();
        var uses = actionExec.Uses.Value;
        if (uses.Length == 0)
        {
            // Empty uses value: the parser already reported an error for this step.
            return;
        }
        var usesLocation = actionExec.UsesKeyRange ?? actionExec.Uses.Range;
        var usesRefLocation = BuildRefLocation(actionExec.Uses.Slice, uses, Config.Utf8Yaml, usesLocation);
        if (uses.StartsWith("docker://"u8))
        {
            if (uses.Length <= "docker://"u8.Length)
            {
                AddStepError(step, "'docker://' must include an image reference", usesLocation);
            }
            else if (uses[^1] == (byte)':')
            {
                // Tag portion is empty: "docker://image:" → flag it
                var imageDisplay = actionExec.Uses.Decode();
                // Remove trailing colon for display (matches actionlint format)
                if (imageDisplay.EndsWith(':'))
                {
                    imageDisplay = imageDisplay[..^1];
                }

                AddStepError(step, $"tag of Docker action should not be empty: \"{imageDisplay}\"", usesLocation);
            }

            return;
        }

        if (uses.StartsWith("./"u8) || uses.StartsWith("../"u8))
        {
            if (uses.IndexOf((byte)'@') >= 0)
            {
                AddStepWarning(step, "local action uses must not contain '@ref'", usesRefLocation);
                return;
            }

            ValidateLocalActionResolution(step, uses, usesLocation);
            return;
        }

        if (!TryParseRemoteUses(uses, out var parsedStep))
        {
            var invalidUsesText = actionExec.Uses.Decode();
            AddStepError(
                step,
                $"'{invalidUsesText}' has invalid reference format; expected owner/repo[/path]@ref",
                usesLocation);
            return;
        }

        if (IsFullCommitSha(parsedStep.Ref))
        {
            return;
        }

        if (IsIgnoredAction(parsedStep.ActionPath, parsedStep.Ref))
        {
            if (Config.Verbose)
            {
                var ignoredUsesText = actionExec.Uses.Decode();
                AddStepInfo(step, $"ignored '{ignoredUsesText}' (matched ignore-actions pattern)", usesLocation);
            }

            return;
        }

        var usesSlice = actionExec.Uses.Slice;
        var message = GetUnpinnedStepMessage(usesSlice, out var decodedUsesText);
        var help = BuildOwnerHintOnce(parsedStep.ActionPath);
        AddStepWarning(step, message, usesRefLocation, PinDiagnosticMetadata.ForUsesRef(decodedUsesText), help);
    }

    /// <summary>
    /// Returns a config-snippet help hint for the given action path's owner, or null if the owner
    /// has already been hinted in this workflow run (deduplication).
    /// Uses a two-level cache: fast byte-span check for the common repeated-owner case,
    /// then HashSet fallback for multi-owner workflows.
    /// </summary>
    private string? BuildOwnerHintOnce(ReadOnlySpan<byte> actionPath)
    {
        if (!TryParseOwnerRepoSegments(actionPath, out var ownerSpan, out _))
        {
            return null;
        }

        // Fast path: if the owner bytes match the last-seen owner, skip entirely (zero allocation)
        if (_lastHintedOwnerBytes is not null
            && ownerSpan.Length == _lastHintedOwnerBytes.Length
            && ownerSpan.SequenceEqual(_lastHintedOwnerBytes))
        {
            return null;
        }

        // Slow path: materialize owner string for HashSet check (case-insensitive dedup)
        var owner = Encoding.UTF8.GetString(ownerSpan);
        if (!_hintedOwners.Add(owner))
        {
            // Already hinted (different case variant) — update last-seen cache to avoid future allocs
            _lastHintedOwnerBytes = ownerSpan.ToArray();
            return null;
        }

        // First time seeing this owner — cache bytes and build hint
        _lastHintedOwnerBytes = ownerSpan.ToArray();
        return $"to ignore this owner, add to .github/seiton.yaml: rules: {{ unpinned-uses: {{ ignore-actions: [{{ owner: \"{owner}/*\" }}] }} }}";
    }

    private string GetUnpinnedStepMessage(Utf8Slice usesSlice, out string decodedUsesText)
    {
        if (_lastUnpinnedStepMessage is not null
            && usesSlice.Offset == _lastUnpinnedStepUsesSlice.Offset
            && usesSlice.Length == _lastUnpinnedStepUsesSlice.Length)
        {
            decodedUsesText = _lastDecodedUsesText!;
            return _lastUnpinnedStepMessage;
        }

        // Different slice — check content equality for same-text-different-position
        if (_lastUnpinnedStepMessage is not null
            && Config.Utf8Yaml is not null
            && usesSlice.Length == _lastUnpinnedStepUsesSlice.Length
            && usesSlice.AsSpan(Config.Utf8Yaml).SequenceEqual(_lastUnpinnedStepUsesSlice.AsSpan(Config.Utf8Yaml)))
        {
            _lastUnpinnedStepUsesSlice = usesSlice;
            decodedUsesText = _lastDecodedUsesText!;
            return _lastUnpinnedStepMessage;
        }

        var usesText = Decode(usesSlice);
        var url = ActionRefHelpers.BuildGitHubUrl(usesText);
        var urlSuffix = url is not null ? $". see {url}" : "";
        var msg = $"'{usesText}' is not pinned to a full-length commit SHA{urlSuffix} (fixable with --fix --enable-pin-network)";
        _lastUnpinnedStepUsesSlice = usesSlice;
        _lastUnpinnedStepMessage = msg;
        _lastDecodedUsesText = usesText;
        decodedUsesText = usesText;
        return msg;
    }

    private static TextRange BuildRefLocation(Utf8Slice usesValue, ReadOnlySpan<byte> uses, byte[] source, TextRange fallback)
    {
        var at = uses.LastIndexOf((byte)'@');
        if (at < 0 || at + 1 >= uses.Length)
        {
            return fallback;
        }

        var startOffset = usesValue.Offset + at;
        var endOffset = usesValue.Offset + usesValue.Length;
        if (startOffset < 0 || endOffset > source.Length || startOffset >= endOffset)
        {
            return fallback;
        }

        var (startLine, startColumn) = ComputeLineColumn(source, startOffset);
        var (endLine, endColumn) = ComputeLineColumn(source, endOffset);

        return new TextRange(
            Start: startOffset,
            Length: endOffset - startOffset,
            StartLine: startLine,
            StartColumn: startColumn,
            EndLine: endLine,
            EndColumn: endColumn);
    }

    private static (int Line, int Column) ComputeLineColumn(byte[] source, int offset)
    {
        var line = 1;
        var column = 1;

        for (var i = 0; i < offset; i++)
        {
            var b = source[i];
            if (b == (byte)'\n')
            {
                line++;
                column = 1;
                continue;
            }

            if (b != (byte)'\r')
            {
                column++;
            }
        }

        return (line, column);
    }

    private void ValidateLocalActionResolution(StepRef step, ReadOnlySpan<byte> uses, TextRange location)
    {
        if (string.IsNullOrEmpty(Config.FilePath)
            || !Path.IsPathFullyQualified(Config.FilePath)
            || !File.Exists(Config.FilePath))
        {
            return;
        }

        var relativePath = DecodeAscii(uses);
        var baseDirectory = ActionRefHelpers.ResolveLocalReferenceBaseDirectory(Config.FilePath, relativePath);
        if (string.IsNullOrEmpty(baseDirectory))
        {
            return;
        }

        var resolvedPath = ActionRefHelpers.NormalizeFullPath(baseDirectory, relativePath);
        if (resolvedPath is null)
        {
            return;
        }

        if (!Directory.Exists(resolvedPath))
        {
            AddStepWarning(step, $"local action path '{relativePath}' does not exist", location);
            return;
        }

        var hasMetadata = File.Exists(Path.Combine(resolvedPath, "action.yml"))
            || File.Exists(Path.Combine(resolvedPath, "action.yaml"));

        if (!hasMetadata)
        {
            AddStepWarning(step, $"local action path '{relativePath}' is missing action.yml or action.yaml", location);
        }
    }

    private static string DecodeAscii(ReadOnlySpan<byte> utf8)
    {
        var chars = new char[utf8.Length];
        for (var i = 0; i < utf8.Length; i++)
        {
            chars[i] = (char)utf8[i];
        }

        return new string(chars);
    }

    private bool IsIgnoredAction(ReadOnlySpan<byte> actionPath, ReadOnlySpan<byte> actionRef)
    {
        if (_ignoreEntries.Length == 0)
        {
            return false;
        }

        if (!TryParseOwnerRepoSegments(actionPath, out var owner, out var repo))
        {
            return false;
        }

        var need = owner.Length + 1 + repo.Length;
        Span<byte> scratch = stackalloc byte[need <= 128 ? need : 0];
        byte[]? rented = null;
        if (need > 128)
        {
            rented = ArrayPool<byte>.Shared.Rent(need);
            scratch = rented.AsSpan(0, need);
        }

        try
        {
            // Write ASCII-lowercased owner/repo key directly (avoids re-parsing in TryGetOwnerRepoPolicyKey)
            var o = 0;
            for (var i = 0; i < owner.Length; i++)
            {
                var b = owner[i];
                scratch[o++] = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
            }

            scratch[o++] = (byte)'/';
            for (var i = 0; i < repo.Length; i++)
            {
                var b = repo[i];
                scratch[o++] = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
            }

            return MatchAnyIgnoreEntry(scratch[..need], actionRef);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private bool MatchAnyIgnoreEntry(ReadOnlySpan<byte> ownerRepoKeyUtf8, ReadOnlySpan<byte> actionRef)
    {
        for (var i = 0; i < _ignoreEntries.Length; i++)
        {
            ref readonly var entry = ref _ignoreEntries[i];
            if (!WildcardMatchUsesPolicy(ownerRepoKeyUtf8, entry.PatternUtf8))
            {
                continue;
            }

            // Owner-only entry (Refs is null): ignore all refs
            if (entry.RefsUtf8 is null)
            {
                return true;
            }

            // Ref-conditional entry: check if action ref matches any configured ref (case-sensitive exact match)
            for (var j = 0; j < entry.RefsUtf8.Length; j++)
            {
                if (actionRef.SequenceEqual(entry.RefsUtf8[j]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Internal ignore entry: pre-encoded pattern and optional ref constraints.</summary>
    private readonly struct IgnoreEntry(byte[] patternUtf8, byte[][]? refsUtf8)
    {
        public readonly byte[] PatternUtf8 = patternUtf8;
        public readonly byte[][]? RefsUtf8 = refsUtf8;
    }
}

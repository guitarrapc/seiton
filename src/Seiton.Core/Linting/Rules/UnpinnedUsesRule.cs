using System.Buffers;
using System.Text;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Linting.ActionRefHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags action references not pinned to a full commit SHA.</summary>
public sealed class UnpinnedUsesRule() : RuleBase(RuleId.UnpinnedUses)
{
    // Cache last-produced "not pinned" message and decoded text to avoid repeated string allocation
    // for the same action ref (common: all steps use the same action)
    private Utf8Slice _lastUnpinnedStepUsesSlice;
    private string? _lastUnpinnedStepMessage;
    private string? _lastDecodedUsesText;

    private byte[][] _ignoreActionsUtf8 = [];

    public override string Name => "Unpinned Uses Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        var ruleConfig = config.GetRuleConfig(Id);
        var ignoreActions = ruleConfig?.IgnoreActions;
        if (ignoreActions is { Count: > 0 })
        {
            _ignoreActionsUtf8 = new byte[ignoreActions.Count][];
            for (var i = 0; i < ignoreActions.Count; i++)
            {
                _ignoreActionsUtf8[i] = Encoding.UTF8.GetBytes(ignoreActions[i].ToLowerInvariant());
            }
        }
        else
        {
            _ignoreActionsUtf8 = [];
        }
    }

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        // Clear per-source cache — slice offsets are invalid across different source bytes.
        _lastUnpinnedStepUsesSlice = default;
        _lastUnpinnedStepMessage = null;
        _lastDecodedUsesText = null;
    }

    public override void VisitJobPre(Job job)
    {
        var workflowCall = job.WorkflowCall;
        if (workflowCall is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var uses = Arena.GetStringValue(workflowCall.Uses);
        var usesLocation = BuildUsesLocation(workflowCall);
        var usesRefLocation = BuildRefLocation(Arena.GetStringSlice(workflowCall.Uses), uses, Config.Utf8Yaml, usesLocation);
        if (uses.StartsWith("./"u8))
        {
            if (uses.IndexOf((byte)'@') >= 0)
            {
                var localJobId = Decode(Arena.GetStringSlice(job.Id));
                AddJobWarning(
                    job,
                    $"jobs.'{localJobId}'.uses local reusable workflow reference must not contain '@ref'",
                    usesRefLocation);
            }

            return;
        }

        // ../ prefix is not valid for reusable workflows (only ./ is allowed)
        if (uses.StartsWith("../"u8))
        {
            var usesStr = Decode(Arena.GetStringSlice(workflowCall.Uses));
            AddJobError(
                job,
                $"reusable workflow call \"{usesStr}\" at \"uses\" is not following the format \"owner/repo/path/to/workflow.yml@ref\" nor \"./path/to/workflow.yml\". see https://docs.github.com/en/actions/learn-github-actions/reusing-workflows for more details",
                usesLocation);
            return;
        }

        if (!TryParseRemoteUses(uses, out var parsedJob))
        {
            var formatJobId = Decode(Arena.GetStringSlice(job.Id));
            var invalidUsesText = Decode(Arena.GetStringSlice(workflowCall.Uses));
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

        if (IsIgnoredAction(parsedJob.ActionPath))
        {
            if (Config.Verbose)
            {
                var ignoredUsesText = Decode(Arena.GetStringSlice(workflowCall.Uses));
                AddJobInfo(job, $"ignored '{ignoredUsesText}' (matched ignore-actions pattern)", usesLocation);
            }

            return;
        }

        var jobId = Decode(Arena.GetStringSlice(job.Id));
        var usesText = Decode(Arena.GetStringSlice(workflowCall.Uses));
        var url = ActionRefHelpers.BuildGitHubUrl(usesText);
        var urlSuffix = url is not null ? $". see {url}" : "";
        AddJobWarning(job, $"jobs.'{jobId}'.uses '{usesText}' is not pinned to a full-length commit SHA{urlSuffix} (fixable with --fix --enable-pin-network)", usesRefLocation, PinDiagnosticMetadata.ForUsesRef(usesText));
    }

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecAction actionExec || Config.Utf8Yaml is null)
        {
            return;
        }

        var uses = Arena.GetStringValue(actionExec.Uses);
        if (uses.Length == 0)
        {
            // Empty uses value: the parser already reported an error for this step.
            return;
        }
        var usesLocation = actionExec.UsesKeyRange ?? Arena.GetStringRange(actionExec.Uses);
        var usesRefLocation = BuildRefLocation(Arena.GetStringSlice(actionExec.Uses), uses, Config.Utf8Yaml, usesLocation);
        if (uses.StartsWith("docker://"u8))
        {
            if (uses.Length <= "docker://"u8.Length)
            {
                AddStepError(step, "'docker://' must include an image reference", usesLocation);
            }
            else if (uses[^1] == (byte)':')
            {
                // Tag portion is empty: "docker://image:" → flag it
                var imageSlice = Arena.GetStringSlice(actionExec.Uses);
                var imageDisplay = Decode(imageSlice);
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
            var invalidUsesText = Decode(Arena.GetStringSlice(actionExec.Uses));
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

        if (IsIgnoredAction(parsedStep.ActionPath))
        {
            if (Config.Verbose)
            {
                var ignoredUsesText = Decode(Arena.GetStringSlice(actionExec.Uses));
                AddStepInfo(step, $"ignored '{ignoredUsesText}' (matched ignore-actions pattern)", usesLocation);
            }

            return;
        }

        var usesSlice = Arena.GetStringSlice(actionExec.Uses);
        var message = GetUnpinnedStepMessage(usesSlice, out var decodedUsesText);
        AddStepWarning(step, message, usesRefLocation, PinDiagnosticMetadata.ForUsesRef(decodedUsesText));
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

    private void ValidateLocalActionResolution(Step step, ReadOnlySpan<byte> uses, TextRange location)
    {
        if (string.IsNullOrEmpty(Config.FilePath)
            || !Path.IsPathFullyQualified(Config.FilePath)
            || !File.Exists(Config.FilePath))
        {
            return;
        }

        var relativePath = DecodeAscii(uses);
        var localPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var baseDirectory = ResolveLocalReferenceBaseDirectory(Config.FilePath, localPath);
        if (string.IsNullOrEmpty(baseDirectory))
        {
            return;
        }

        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(Path.Combine(baseDirectory, TrimCurrentDirectoryPrefix(localPath)));
        }
        catch
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

    private static string ResolveLocalReferenceBaseDirectory(string workflowFilePath, string localPath)
    {
        var workflowDirectory = Path.GetDirectoryName(workflowFilePath);
        if (string.IsNullOrEmpty(workflowDirectory))
        {
            return string.Empty;
        }

        if (localPath.StartsWith($".{Path.DirectorySeparatorChar}.github{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && TryGetRepositoryRoot(workflowFilePath, out var repositoryRoot))
        {
            return repositoryRoot;
        }

        return workflowDirectory;
    }

    private static bool TryGetRepositoryRoot(string workflowFilePath, out string repositoryRoot)
    {
        var separator = Path.DirectorySeparatorChar;
        var marker = $"{separator}.github{separator}workflows{separator}";
        var index = workflowFilePath.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            repositoryRoot = workflowFilePath[..index];
            return true;
        }

        var markerAtEnd = $"{separator}.github{separator}workflows";
        if (workflowFilePath.EndsWith(markerAtEnd, StringComparison.OrdinalIgnoreCase))
        {
            repositoryRoot = workflowFilePath[..^markerAtEnd.Length];
            return true;
        }

        repositoryRoot = string.Empty;
        return false;
    }

    private static string TrimCurrentDirectoryPrefix(string path)
    {
        if (path.StartsWith($".{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return path.Substring(2);
        }

        return path;
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

    private bool IsIgnoredAction(ReadOnlySpan<byte> actionPath)
    {
        if (_ignoreActionsUtf8.Length == 0)
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

            return MatchAnyIgnorePattern(scratch[..need]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private bool MatchAnyIgnorePattern(ReadOnlySpan<byte> ownerRepoKeyUtf8)
    {
        for (var i = 0; i < _ignoreActionsUtf8.Length; i++)
        {
            if (WildcardMatchUsesPolicy(ownerRepoKeyUtf8, _ignoreActionsUtf8[i]))
            {
                return true;
            }
        }

        return false;
    }
}

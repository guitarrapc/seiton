using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Linting.ActionRefHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class UnpinnedUsesRule : RuleBase
{
    // Cache last-produced "not pinned" message to avoid repeated string allocation
    // for the same action ref (common: all steps use the same action)
    private Utf8Slice _lastUnpinnedStepUsesSlice;
    private string? _lastUnpinnedStepMessage;

    public override string Id => "unpinned-uses";

    public override string Name => "Unpinned Uses Rule";

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
        if (uses.StartsWith("./"u8) || uses.StartsWith("../"u8))
        {
            if (uses.IndexOf((byte)'@') >= 0)
            {
                var localJobId = Decode(Arena.GetStringSlice(job.Id));
                AddJobWarning(
                    job,
                    $"job '{localJobId}' local reusable workflow uses must not contain '@ref'",
                    usesRefLocation);
            }

            return;
        }

        if (!TryParseRemoteUses(uses, out var parsedJob))
        {
            var formatJobId = Decode(Arena.GetStringSlice(job.Id));
            var invalidUsesText = Decode(Arena.GetStringSlice(workflowCall.Uses));
            AddJobWarning(
                job,
                $"job '{formatJobId}' reusable workflow uses '{invalidUsesText}' has invalid reference format; expected owner/repo/path@ref",
                usesLocation);
            return;
        }

        if (IsFullCommitSha(parsedJob.Ref))
        {
            return;
        }

        var jobId = Decode(Arena.GetStringSlice(job.Id));
        var usesText = Decode(Arena.GetStringSlice(workflowCall.Uses));
        AddJobWarning(job, $"job '{jobId}' reusable workflow uses '{usesText}' is not pinned to a full-length commit SHA", usesRefLocation, PinDiagnosticMetadata.ForUsesRef(usesText));
    }

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecAction actionExec || Config.Utf8Yaml is null)
        {
            return;
        }

        var uses = Arena.GetStringValue(actionExec.Uses);
        var usesLocation = actionExec.UsesKeyRange ?? Arena.GetStringRange(actionExec.Uses);
        var usesRefLocation = BuildRefLocation(Arena.GetStringSlice(actionExec.Uses), uses, Config.Utf8Yaml, usesLocation);
        if (uses.StartsWith("docker://"u8))
        {
            if (uses.Length <= "docker://"u8.Length)
            {
                AddStepWarning(step, "action uses 'docker://' must include an image reference", usesLocation);
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
            AddStepWarning(
                step,
                $"action uses '{invalidUsesText}' has invalid reference format; expected owner/repo[/path]@ref",
                usesLocation);
            return;
        }

        if (IsFullCommitSha(parsedStep.Ref))
        {
            return;
        }

        var usesSlice = Arena.GetStringSlice(actionExec.Uses);
        var message = GetUnpinnedStepMessage(usesSlice);
        AddStepWarning(step, message, usesRefLocation, PinDiagnosticMetadata.ForUsesRef(Decode(usesSlice)));
    }

    private string GetUnpinnedStepMessage(Utf8Slice usesSlice)
    {
        if (_lastUnpinnedStepMessage is not null
            && usesSlice.Offset == _lastUnpinnedStepUsesSlice.Offset
            && usesSlice.Length == _lastUnpinnedStepUsesSlice.Length)
        {
            return _lastUnpinnedStepMessage;
        }

        // Different slice — check content equality for same-text-different-position
        if (_lastUnpinnedStepMessage is not null
            && Config.Utf8Yaml is not null
            && usesSlice.Length == _lastUnpinnedStepUsesSlice.Length
            && usesSlice.AsSpan(Config.Utf8Yaml).SequenceEqual(_lastUnpinnedStepUsesSlice.AsSpan(Config.Utf8Yaml)))
        {
            _lastUnpinnedStepUsesSlice = usesSlice;
            return _lastUnpinnedStepMessage;
        }

        var usesText = Decode(usesSlice);
        var msg = $"action uses '{usesText}' is not pinned to a full-length commit SHA";
        _lastUnpinnedStepUsesSlice = usesSlice;
        _lastUnpinnedStepMessage = msg;
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
}

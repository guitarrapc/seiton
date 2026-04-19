using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class UnpinnedUsesRule : RuleBase
{
    public override string Id => "unpinned-uses";

    public override string Name => "Unpinned Uses Rule";

    public override void VisitJobPre(Job job)
    {
        var workflowCall = job.WorkflowCall;
        if (workflowCall is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var usesLocation = BuildUsesLocation(workflowCall);
        var uses = workflowCall.Uses.Value.AsSpan(Config.Utf8Yaml);
        if (uses.StartsWith("./"u8) || uses.StartsWith("../"u8))
        {
            if (uses.IndexOf((byte)'@') >= 0)
            {
                var localJobId = Decode(job.Id.Value);
                AddJobWarning(
                    job,
                    $"job '{localJobId}' local reusable workflow uses must not contain '@ref'",
                    usesLocation);
            }

            return;
        }

        if (!HasRemoteActionUsesFormat(uses))
        {
            var formatJobId = Decode(job.Id.Value);
            var invalidUsesText = Decode(workflowCall.Uses.Value);
            AddJobWarning(
                job,
                $"job '{formatJobId}' reusable workflow uses '{invalidUsesText}' has invalid reference format; expected owner/repo/path@ref",
                usesLocation);
            return;
        }

        if (IsFullLengthCommitShaPinned(uses))
        {
            return;
        }

        var jobId = Decode(job.Id.Value);
        var usesText = Decode(workflowCall.Uses.Value);
        AddJobWarning(job, $"job '{jobId}' reusable workflow uses '{usesText}' is not pinned to a full-length commit SHA", usesLocation);
    }

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecAction actionExec || Config.Utf8Yaml is null)
        {
            return;
        }

        var usesLocation = actionExec.UsesKeyRange ?? actionExec.Uses.Range;
        var uses = actionExec.Uses.Value.AsSpan(Config.Utf8Yaml);
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
                AddStepWarning(step, "local action uses must not contain '@ref'", usesLocation);
                return;
            }

            ValidateLocalActionResolution(step, uses, usesLocation);
            return;
        }

        if (!HasRemoteActionUsesFormat(uses))
        {
            var invalidUsesText = Decode(actionExec.Uses.Value);
            AddStepWarning(
                step,
                $"action uses '{invalidUsesText}' has invalid reference format; expected owner/repo[/path]@ref",
                usesLocation);
            return;
        }

        if (IsFullLengthCommitShaPinned(uses))
        {
            return;
        }

        var usesText = Decode(actionExec.Uses.Value);
        AddStepWarning(step, $"action uses '{usesText}' is not pinned to a full-length commit SHA", usesLocation);
    }

    void ValidateLocalActionResolution(Step step, ReadOnlySpan<byte> uses, TextRange location)
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

    static bool HasRemoteActionUsesFormat(ReadOnlySpan<byte> uses)
    {
        var at = uses.LastIndexOf((byte)'@');
        if (at <= 0 || at + 1 >= uses.Length)
        {
            return false;
        }

        var left = uses[..at];
        var firstSlash = left.IndexOf((byte)'/');
        if (firstSlash <= 0 || firstSlash + 1 >= left.Length)
        {
            return false;
        }

        var secondSegment = left[(firstSlash + 1)..];
        if (secondSegment.IsEmpty)
        {
            return false;
        }

        var nextSlash = secondSegment.IndexOf((byte)'/');
        if (nextSlash == 0)
        {
            return false;
        }

        return true;
    }

    static bool IsFullLengthCommitShaPinned(ReadOnlySpan<byte> uses)
    {
        var at = uses.LastIndexOf((byte)'@');
        if (at < 0 || at + 1 >= uses.Length)
        {
            return false;
        }

        var reference = uses[(at + 1)..];
        if (reference.Length != 40)
        {
            return false;
        }

        for (var i = 0; i < reference.Length; i++)
        {
            var b = reference[i];
            var isDigit = b is >= (byte)'0' and <= (byte)'9';
            var isLowerHex = b is >= (byte)'a' and <= (byte)'f';
            var isUpperHex = b is >= (byte)'A' and <= (byte)'F';
            if (!isDigit && !isLowerHex && !isUpperHex)
            {
                return false;
            }
        }

        return true;
    }

    static string ResolveLocalReferenceBaseDirectory(string workflowFilePath, string localPath)
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

    static bool TryGetRepositoryRoot(string workflowFilePath, out string repositoryRoot)
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

    static string TrimCurrentDirectoryPrefix(string path)
    {
        if (path.StartsWith($".{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return path.Substring(2);
        }

        return path;
    }

    static string DecodeAscii(ReadOnlySpan<byte> utf8)
    {
        var chars = new char[utf8.Length];
        for (var i = 0; i < utf8.Length; i++)
        {
            chars[i] = (char)utf8[i];
        }

        return new string(chars);
    }
}
